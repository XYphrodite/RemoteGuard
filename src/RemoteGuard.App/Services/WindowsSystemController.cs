using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;

namespace RemoteGuard.App.Services;

public sealed class WindowsSystemController : ISystemController
{
    public bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<ServiceProbeResult> ProbeServiceAsync(
        string serviceName,
        IReadOnlyList<string> processNames,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            ServiceController? match = null;
            var all = ServiceController.GetServices();
            try
            {
                foreach (var sc in all)
                {
                    if (string.Equals(sc.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        match = sc;
                        break;
                    }
                }

                var exists = match is not null;
                var running = false;
                var pending = false;
                string? startMode = null;

                if (match is not null)
                {
                    var status = match.Status;
                    running = status is ServiceControllerStatus.Running
                        or ServiceControllerStatus.Paused
                        or ServiceControllerStatus.ContinuePending
                        or ServiceControllerStatus.PausePending;
                    pending = status is ServiceControllerStatus.StartPending
                        or ServiceControllerStatus.StopPending;

                    try
                    {
                        startMode = match.StartType.ToString();
                    }
                    catch (Exception)
                    {
                        startMode = null;
                    }
                }

                var procCount = 0;
                foreach (var procName in processNames)
                {
                    var procs = Process.GetProcessesByName(procName);
                    try
                    {
                        procCount += procs.Length;
                    }
                    finally
                    {
                        foreach (var p in procs) p.Dispose();
                    }
                }

                return new ServiceProbeResult(exists, running, pending, startMode, procCount);
            }
            finally
            {
                foreach (var sc in all) sc.Dispose();
            }
        }, ct);
    }

    public Task StopServiceWithDependentsAsync(
        string serviceName,
        TimeSpan timeout,
        Action<string> log,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var sc = FindService(serviceName);
            if (sc is null)
            {
                log($"Служба {serviceName} не установлена — пропуск.");
                return;
            }

            StopServiceRecursive(sc, timeout, log, ct);
        }, ct);
    }

    public Task StartServiceAsync(
        string serviceName,
        TimeSpan timeout,
        Action<string> log,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var sc = FindService(serviceName);
            if (sc is null)
                throw new InvalidOperationException($"Служба {serviceName} не установлена в системе.");

            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Running)
            {
                log($"Служба {serviceName} уже запущена.");
                return;
            }

            try
            {
                if (sc.StartType == ServiceStartMode.Disabled)
                    throw new InvalidOperationException(
                        $"Служба {serviceName} отключена в Windows (StartType = Disabled). Измени тип запуска в services.msc.");
            }
            catch (Win32Exception)
            {
                // Ignore if querying StartType fails; Start() will report any real issue.
            }

            if (sc.Status != ServiceControllerStatus.StartPending)
            {
                log($"Запускаем службу {serviceName}…");
                sc.Start();
            }
            else
            {
                log($"Ожидаем завершения запуска службы {serviceName}…");
            }

            WaitForStatus(sc, ServiceControllerStatus.Running, timeout, ct);
            log($"Служба {serviceName} запущена.");
        }, ct);
    }

    public Task<int> KillProcessesAsync(
        IReadOnlyList<string> processNames,
        Action<string> log,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var killed = 0;
            foreach (var procName in processNames)
            {
                ct.ThrowIfCancellationRequested();
                var procs = Process.GetProcessesByName(procName);
                try
                {
                    foreach (var p in procs)
                    {
                        try
                        {
                            if (p.HasExited) continue;
                            var pid = p.Id;
                            log($"Завершаем процесс {procName}.exe (PID {pid})…");
                            try
                            {
                                p.Kill(entireProcessTree: true);
                            }
                            catch (Exception)
                            {
                                p.Kill();
                            }

                            p.WaitForExit(3000);
                            killed++;
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                        {
                            // Process may have exited between GetProcesses and Kill, or access denied.
                            log($"Не удалось завершить {procName}.exe: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    foreach (var p in procs) p.Dispose();
                }
            }
            return killed;
        }, ct);
    }

    public Task<string?> FindTailscaleCliPathAsync(CancellationToken ct)
    {
        return Task.Run<string?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            string[] candidates =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe"),
            ];

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var full = Path.Combine(dir, "tailscale.exe");
                if (File.Exists(full))
                    return full;
            }

            return null;
        }, ct);
    }

    public async Task<CliCommandResult> RunProcessAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* best effort */ }
            throw new System.TimeoutException($"Команда «{Path.GetFileName(fileName)} {arguments}» превысила таймаут ({timeout.TotalSeconds:N0} с).");
        }

        var stdout = await stdOutTask.ConfigureAwait(false);
        var stderr = await stdErrTask.ConfigureAwait(false);
        return new CliCommandResult(process.ExitCode, stdout.Trim(), stderr.Trim());
    }

    private static void StopServiceRecursive(
        ServiceController sc,
        TimeSpan timeout,
        Action<string> log,
        CancellationToken ct)
    {
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Stopped)
            return;

        foreach (var dep in sc.DependentServices)
        {
            using (dep)
            {
                dep.Refresh();
                if (dep.Status != ServiceControllerStatus.Stopped)
                {
                    log($"Останавливаем зависимую службу {dep.ServiceName}…");
                    StopServiceRecursive(dep, timeout, log, ct);
                }
            }
        }

        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Stopped)
            return;

        if (sc.Status != ServiceControllerStatus.StopPending)
        {
            if (!sc.CanStop)
                throw new InvalidOperationException($"Служба {sc.ServiceName} сейчас не поддерживает остановку.");

            log($"Останавливаем службу {sc.ServiceName}…");
            sc.Stop();
        }
        else
        {
            log($"Ожидаем остановку службы {sc.ServiceName}…");
        }

        WaitForStatus(sc, ServiceControllerStatus.Stopped, timeout, ct);
        log($"Служба {sc.ServiceName} остановлена.");
    }

    private static void WaitForStatus(
        ServiceController sc,
        ServiceControllerStatus desired,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            sc.Refresh();
            if (sc.Status == desired)
                return;

            if (DateTime.UtcNow >= deadline)
                throw new System.TimeoutException($"Служба {sc.ServiceName} не перешла в состояние {desired} за {timeout.TotalSeconds:N0} с (текущее: {sc.Status}).");

            Thread.Sleep(200);
        }
    }

    private static ServiceController? FindService(string serviceName)
    {
        var all = ServiceController.GetServices();
        ServiceController? found = null;
        foreach (var sc in all)
        {
            if (found is null && string.Equals(sc.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase))
                found = sc;
            else
                sc.Dispose();
        }
        return found;
    }
}
