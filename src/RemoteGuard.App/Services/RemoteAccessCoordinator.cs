using System.Text.Json;

namespace RemoteGuard.App.Services;

/// <summary>
/// Probes and toggles remote access services (Tailscale, SSH, RDP, AnyDesk)
/// while keeping a timestamped action log.
/// </summary>
public sealed class RemoteAccessCoordinator
{
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TailscaleProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly ISystemController _sys;
    private readonly IReadOnlyList<RemoteServiceDefinition> _definitions;
    private readonly SemaphoreSlim _opLock = new(1, 1);

    public RemoteAccessCoordinator(
        ISystemController sys,
        IReadOnlyList<RemoteServiceDefinition>? definitions = null)
    {
        _sys = sys;
        _definitions = definitions ?? RemoteServiceDefinition.Defaults;
    }

    public event Action<LogEntry>? LogAdded;

    public bool IsAdministrator => _sys.IsAdministrator();

    public async Task<IReadOnlyList<ServiceStatusSnapshot>> RefreshAllAsync(CancellationToken ct = default)
    {
        var results = new List<ServiceStatusSnapshot>(_definitions.Count);
        foreach (var def in _definitions)
        {
            results.Add(await ProbeSingleAsync(def, ct).ConfigureAwait(false));
        }
        return results;
    }

    public async Task<IReadOnlyList<ServiceStatusSnapshot>> SetServiceEnabledAsync(
        RemoteServiceId id,
        bool enable,
        CancellationToken ct = default)
    {
        var def = _definitions.FirstOrDefault(d => d.Id == id)
            ?? throw new ArgumentOutOfRangeException(nameof(id));

        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EmitLog(LogLevel.Info, enable
                ? $"Включаем {def.DisplayName}…"
                : $"Отключаем {def.DisplayName}…");

            await ApplySingleInternalAsync(def, enable, ct).ConfigureAwait(false);
            return await RefreshAllAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmitLog(LogLevel.Error, $"{def.DisplayName}: {ex.Message}");
            return await RefreshAllAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    public async Task<IReadOnlyList<ServiceStatusSnapshot>> SetAllEnabledAsync(
        bool enable,
        CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EmitLog(LogLevel.Info, enable
                ? "=== Включаем все службы удалённого доступа ==="
                : "=== Блокируем весь удалённый доступ ===");

            var errors = 0;
            foreach (var def in _definitions)
            {
                try
                {
                    await ApplySingleInternalAsync(def, enable, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errors++;
                    EmitLog(LogLevel.Error, $"{def.DisplayName}: {ex.Message}");
                }
            }

            var snapshots = await RefreshAllAsync(ct).ConfigureAwait(false);
            if (errors == 0)
            {
                EmitLog(LogLevel.Success, enable
                    ? "Готово: все установленные службы включены."
                    : "Готово: весь удалённый доступ отключён.");
            }
            else
            {
                EmitLog(LogLevel.Warning, $"Завершено с ошибками ({errors}). Проверь статусы и журнал.");
            }

            return snapshots;
        }
        finally
        {
            _opLock.Release();
        }
    }

    public static OverallAccessState ComputeOverallState(IReadOnlyList<ServiceStatusSnapshot> snapshots)
    {
        var installed = snapshots.Where(s => s.IsInstalled).ToList();
        if (installed.Count == 0)
            return OverallAccessState.Unknown;

        var activeCount = installed.Count(s => s.IsEnabled);
        if (activeCount == 0)
            return OverallAccessState.AllBlocked;
        if (activeCount == installed.Count)
            return OverallAccessState.AllEnabled;
        return OverallAccessState.Partial;
    }

    private async Task ApplySingleInternalAsync(
        RemoteServiceDefinition def,
        bool enable,
        CancellationToken ct)
    {
        var probe = await _sys.ProbeServiceAsync(def.ServiceName, def.ProcessNames, ct).ConfigureAwait(false);
        var isInstalled = probe.ServiceExists || probe.RunningProcessCount > 0;
        if (!isInstalled)
        {
            EmitLog(LogLevel.Warning, $"{def.DisplayName}: не установлено на этом ПК, пропуск.");
            return;
        }

        if (def.UsesTailscaleCli)
        {
            await ApplyTailscaleAsync(def, probe, enable, ct).ConfigureAwait(false);
            return;
        }

        if (enable)
        {
            if (!probe.ServiceExists)
            {
                EmitLog(LogLevel.Warning, $"{def.DisplayName}: служба {def.ServiceName} не найдена.");
                return;
            }

            await _sys.StartServiceAsync(
                def.ServiceName,
                ServiceTimeout,
                msg => EmitLog(LogLevel.Info, msg),
                ct).ConfigureAwait(false);

            EmitLog(LogLevel.Success, $"{def.DisplayName} включён.");
        }
        else
        {
            if (probe.ServiceExists && (probe.ServiceRunning || probe.ServicePending))
            {
                await _sys.StopServiceWithDependentsAsync(
                    def.ServiceName,
                    ServiceTimeout,
                    msg => EmitLog(LogLevel.Info, msg),
                    ct).ConfigureAwait(false);
            }

            if (def.ProcessNames.Length > 0)
            {
                var killed = await _sys.KillProcessesAsync(
                    def.ProcessNames,
                    msg => EmitLog(LogLevel.Info, msg),
                    ct).ConfigureAwait(false);
                if (killed > 0)
                    EmitLog(LogLevel.Info, $"{def.DisplayName}: закрыто процессов — {killed}.");
            }

            EmitLog(LogLevel.Success, $"{def.DisplayName} отключён.");
        }
    }

    private async Task ApplyTailscaleAsync(
        RemoteServiceDefinition def,
        ServiceProbeResult probe,
        bool enable,
        CancellationToken ct)
    {
        var cliPath = await _sys.FindTailscaleCliPathAsync(ct).ConfigureAwait(false);

        if (enable)
        {
            if (!probe.ServiceExists)
            {
                EmitLog(LogLevel.Warning, $"{def.DisplayName}: служба {def.ServiceName} не найдена.");
                return;
            }

            await _sys.StartServiceAsync(
                def.ServiceName,
                ServiceTimeout,
                msg => EmitLog(LogLevel.Info, msg),
                ct).ConfigureAwait(false);

            if (cliPath is not null)
            {
                var connected = await IsTailscaleBackendRunningAsync(cliPath, ct).ConfigureAwait(false);
                if (connected != true)
                {
                    EmitLog(LogLevel.Info, "Выполняем tailscale up…");
                    var res = await _sys.RunProcessAsync(cliPath, "up", CliTimeout, ct).ConfigureAwait(false);
                    if (res.ExitCode != 0)
                    {
                        var err = string.IsNullOrWhiteSpace(res.StdErr) ? res.StdOut : res.StdErr;
                        throw new InvalidOperationException($"tailscale up завершился с кодом {res.ExitCode}: {err}");
                    }
                    EmitLog(LogLevel.Info, "tailscale up выполнен.");
                }
            }
            else
            {
                EmitLog(LogLevel.Warning, "tailscale.exe не найден, запущена только служба Tailscale.");
            }

            EmitLog(LogLevel.Success, "Tailscale VPN включён.");
        }
        else
        {
            if (probe.ServiceRunning && cliPath is not null)
            {
                EmitLog(LogLevel.Info, "Выполняем tailscale down…");
                try
                {
                    var res = await _sys.RunProcessAsync(cliPath, "down", CliTimeout, ct).ConfigureAwait(false);
                    if (res.ExitCode != 0)
                    {
                        var err = string.IsNullOrWhiteSpace(res.StdErr) ? res.StdOut : res.StdErr;
                        EmitLog(LogLevel.Warning, $"tailscale down вернул код {res.ExitCode}: {err}");
                    }
                    else
                    {
                        EmitLog(LogLevel.Info, "tailscale down выполнен.");
                    }
                }
                catch (Exception ex)
                {
                    EmitLog(LogLevel.Warning, $"Не удалось выполнить tailscale down: {ex.Message}");
                }
            }

            if (probe.ServiceExists && (probe.ServiceRunning || probe.ServicePending))
            {
                await _sys.StopServiceWithDependentsAsync(
                    def.ServiceName,
                    ServiceTimeout,
                    msg => EmitLog(LogLevel.Info, msg),
                    ct).ConfigureAwait(false);
            }

            EmitLog(LogLevel.Success, "Tailscale VPN отключён.");
        }
    }

    private async Task<ServiceStatusSnapshot> ProbeSingleAsync(
        RemoteServiceDefinition def,
        CancellationToken ct)
    {
        var probe = await _sys.ProbeServiceAsync(def.ServiceName, def.ProcessNames, ct).ConfigureAwait(false);
        var isInstalled = probe.ServiceExists || probe.RunningProcessCount > 0;
        var processRunning = probe.RunningProcessCount > 0;
        var isDisabledMode = string.Equals(probe.StartMode, "Disabled", StringComparison.OrdinalIgnoreCase);

        if (!isInstalled)
        {
            return new ServiceStatusSnapshot(
                def,
                IsInstalled: false,
                ServiceRunning: false,
                ProcessRunning: false,
                TailscaleConnected: null,
                IsEnabled: false,
                IsDisabledStartMode: false,
                DetailText: "Не установлено на этом ПК");
        }

        if (def.UsesTailscaleCli)
        {
            bool? tsConnected = null;
            if (probe.ServiceRunning)
            {
                var cliPath = await _sys.FindTailscaleCliPathAsync(ct).ConfigureAwait(false);
                if (cliPath is not null)
                    tsConnected = await IsTailscaleBackendRunningAsync(cliPath, ct).ConfigureAwait(false);
            }

            var isEnabled = probe.ServiceRunning && tsConnected != false;
            var detail = !probe.ServiceRunning
                ? "Служба остановлена · сеть отключена"
                : tsConnected switch
                {
                    true => "Служба запущена · подключён к tailnet",
                    false => "Служба запущена · tailscale down",
                    null => "Служба запущена",
                };

            return new ServiceStatusSnapshot(
                def,
                IsInstalled: true,
                ServiceRunning: probe.ServiceRunning,
                ProcessRunning: processRunning,
                TailscaleConnected: tsConnected,
                IsEnabled: isEnabled,
                IsDisabledStartMode: isDisabledMode,
                DetailText: detail);
        }

        var enabled = probe.ServiceRunning || processRunning;
        string detailText;
        if (probe.ServiceRunning && processRunning)
            detailText = $"Служба запущена · процессов: {probe.RunningProcessCount}";
        else if (probe.ServiceRunning)
            detailText = "Служба запущена";
        else if (processRunning)
            detailText = $"Служба остановлена, но запущен процесс ({probe.RunningProcessCount})";
        else if (isDisabledMode)
            detailText = "Остановлена (в Windows тип запуска: Отключена)";
        else
            detailText = "Остановлена";

        return new ServiceStatusSnapshot(
            def,
            IsInstalled: true,
            ServiceRunning: probe.ServiceRunning,
            ProcessRunning: processRunning,
            TailscaleConnected: null,
            IsEnabled: enabled,
            IsDisabledStartMode: isDisabledMode,
            DetailText: detailText);
    }

    private async Task<bool?> IsTailscaleBackendRunningAsync(string cliPath, CancellationToken ct)
    {
        try
        {
            var res = await _sys.RunProcessAsync(cliPath, "status --json", TailscaleProbeTimeout, ct).ConfigureAwait(false);
            if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StdOut))
                return null;

            using var doc = JsonDocument.Parse(res.StdOut);
            if (doc.RootElement.TryGetProperty("BackendState", out var stateEl))
            {
                var state = stateEl.GetString();
                if (string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(state, "Stopped", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "NeedsLogin", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        catch (Exception)
        {
            // Status JSON is best-effort; fallback to service status.
        }
        return null;
    }

    private void EmitLog(LogLevel level, string message)
    {
        LogAdded?.Invoke(new LogEntry(DateTimeOffset.Now, level, message));
    }
}
