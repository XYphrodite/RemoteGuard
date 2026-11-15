using RemoteGuard.App.Services;
using Xunit;

namespace RemoteGuard.Tests;

public sealed class CoordinatorTests
{
    private sealed class FakeSystemController : ISystemController
    {
        public Dictionary<string, (bool Exists, bool Running, string StartMode)> Services { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Tailscale"] = (true, true, "Automatic"),
            ["sshd"] = (true, true, "Automatic"),
            ["TermService"] = (true, true, "Automatic"),
            ["AnyDesk"] = (true, false, "Automatic"),
        };

        public Dictionary<string, int> Processes { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AnyDesk"] = 1,
        };

        public string TailscaleBackendState { get; set; } = "Running";
        public string? TailscaleCliPath { get; set; } = @"C:\Program Files\Tailscale\tailscale.exe";
        public HashSet<string> FailOnStart { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> CommandsRun { get; } = [];

        public bool IsAdministrator() => true;

        public Task<ServiceProbeResult> ProbeServiceAsync(
            string serviceName, IReadOnlyList<string> processNames, CancellationToken ct)
        {
            var svc = Services.TryGetValue(serviceName, out var s) ? s : (Exists: false, Running: false, StartMode: "Manual");
            var procs = processNames.Sum(p => Processes.TryGetValue(p, out var count) ? count : 0);
            return Task.FromResult(new ServiceProbeResult(svc.Exists, svc.Running, false, svc.StartMode, procs));
        }

        public Task StopServiceWithDependentsAsync(
            string serviceName, TimeSpan timeout, Action<string> log, CancellationToken ct)
        {
            CommandsRun.Add($"stop:{serviceName}");
            if (Services.TryGetValue(serviceName, out var s))
                Services[serviceName] = (s.Exists, false, s.StartMode);
            log($"stopped {serviceName}");
            return Task.CompletedTask;
        }

        public Task StartServiceAsync(
            string serviceName, TimeSpan timeout, Action<string> log, CancellationToken ct)
        {
            CommandsRun.Add($"start:{serviceName}");
            if (FailOnStart.Contains(serviceName))
                throw new InvalidOperationException($"boom {serviceName}");
            if (Services.TryGetValue(serviceName, out var s))
                Services[serviceName] = (s.Exists, true, s.StartMode);
            log($"started {serviceName}");
            return Task.CompletedTask;
        }

        public Task<int> KillProcessesAsync(
            IReadOnlyList<string> processNames, Action<string> log, CancellationToken ct)
        {
            var killed = 0;
            foreach (var name in processNames)
            {
                if (Processes.TryGetValue(name, out var count) && count > 0)
                {
                    killed += count;
                    Processes[name] = 0;
                    CommandsRun.Add($"kill:{name}");
                }
            }
            return Task.FromResult(killed);
        }

        public Task<string?> FindTailscaleCliPathAsync(CancellationToken ct) =>
            Task.FromResult(TailscaleCliPath);

        public Task<CliCommandResult> RunProcessAsync(
            string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
        {
            CommandsRun.Add($"cli:{arguments}");
            if (arguments == "status --json")
            {
                return Task.FromResult(new CliCommandResult(0, $"{{\"BackendState\":\"{TailscaleBackendState}\"}}", ""));
            }
            if (arguments == "down")
            {
                TailscaleBackendState = "Stopped";
                return Task.FromResult(new CliCommandResult(0, "", ""));
            }
            if (arguments == "up")
            {
                TailscaleBackendState = "Running";
                return Task.FromResult(new CliCommandResult(0, "", ""));
            }
            return Task.FromResult(new CliCommandResult(0, "", ""));
        }
    }

    [Fact]
    public async Task DisableAll_StopsAllServices_RunsTailscaleDown_AndKillsAnyDeskProcess()
    {
        var sys = new FakeSystemController();
        var coord = new RemoteAccessCoordinator(sys);
        var logs = new List<LogEntry>();
        coord.LogAdded += logs.Add;

        var result = await coord.SetAllEnabledAsync(enable: false);

        Assert.Equal(OverallAccessState.AllBlocked, RemoteAccessCoordinator.ComputeOverallState(result));
        Assert.All(result, s => Assert.False(s.IsEnabled));
        Assert.Contains("cli:down", sys.CommandsRun);
        Assert.Contains("stop:Tailscale", sys.CommandsRun);
        Assert.Contains("stop:sshd", sys.CommandsRun);
        Assert.Contains("stop:TermService", sys.CommandsRun);
        Assert.Contains("kill:AnyDesk", sys.CommandsRun);
        Assert.Equal(0, sys.Processes["AnyDesk"]);
        Assert.Contains(logs, l => l.Level == LogLevel.Success && l.Message.Contains("отключён"));
    }

    [Fact]
    public async Task EnableAll_StartsAllServices_AndRunsTailscaleUpWhenDisconnected()
    {
        var sys = new FakeSystemController
        {
            TailscaleBackendState = "Stopped",
        };
        sys.Services["Tailscale"] = (true, false, "Automatic");
        sys.Services["sshd"] = (true, false, "Automatic");
        sys.Services["TermService"] = (true, false, "Automatic");
        sys.Services["AnyDesk"] = (true, false, "Automatic");
        sys.Processes["AnyDesk"] = 0;

        var coord = new RemoteAccessCoordinator(sys);
        var result = await coord.SetAllEnabledAsync(enable: true);

        Assert.Equal(OverallAccessState.AllEnabled, RemoteAccessCoordinator.ComputeOverallState(result));
        Assert.All(result, s => Assert.True(s.IsEnabled));
        Assert.Contains("start:Tailscale", sys.CommandsRun);
        Assert.Contains("cli:up", sys.CommandsRun);
        Assert.Contains("start:sshd", sys.CommandsRun);
        Assert.Contains("start:TermService", sys.CommandsRun);
        Assert.Contains("start:AnyDesk", sys.CommandsRun);
    }

    [Fact]
    public async Task SingleServiceToggle_OnlyAffectsTargetService()
    {
        var sys = new FakeSystemController();
        sys.Processes["AnyDesk"] = 0;
        var coord = new RemoteAccessCoordinator(sys);

        var result = await coord.SetServiceEnabledAsync(RemoteServiceId.Ssh, enable: false);

        Assert.False(result.Single(s => s.Id == RemoteServiceId.Ssh).IsEnabled);
        Assert.True(result.Single(s => s.Id == RemoteServiceId.Tailscale).IsEnabled);
        Assert.True(result.Single(s => s.Id == RemoteServiceId.Rdp).IsEnabled);
        Assert.DoesNotContain("stop:TermService", sys.CommandsRun);
    }

    [Fact]
    public async Task AnyDeskProcessRunningWithoutService_ReportsEnabled()
    {
        var sys = new FakeSystemController();
        var coord = new RemoteAccessCoordinator(sys);

        var result = await coord.RefreshAllAsync();
        var anydesk = result.Single(s => s.Id == RemoteServiceId.AnyDesk);

        Assert.True(anydesk.IsEnabled);
        Assert.True(anydesk.ProcessRunning);
        Assert.False(anydesk.ServiceRunning);
        Assert.Contains("процесс", anydesk.DetailText);
    }

    [Fact]
    public async Task TailscaleDownWhileServiceRunning_ReportsDisabled()
    {
        var sys = new FakeSystemController
        {
            TailscaleBackendState = "Stopped",
        };
        var coord = new RemoteAccessCoordinator(sys);

        var result = await coord.RefreshAllAsync();
        var ts = result.Single(s => s.Id == RemoteServiceId.Tailscale);

        Assert.False(ts.IsEnabled);
        Assert.True(ts.ServiceRunning);
        Assert.Equal(false, ts.TailscaleConnected);
    }

    [Fact]
    public async Task ErrorOnOneService_ContinuesOtherServicesAndLogsError()
    {
        var sys = new FakeSystemController();
        sys.Services["sshd"] = (true, false, "Automatic");
        sys.Services["TermService"] = (true, false, "Automatic");
        sys.FailOnStart.Add("sshd");

        var coord = new RemoteAccessCoordinator(sys);
        var logs = new List<LogEntry>();
        coord.LogAdded += logs.Add;

        var result = await coord.SetAllEnabledAsync(enable: true);

        Assert.False(result.Single(s => s.Id == RemoteServiceId.Ssh).IsEnabled);
        Assert.True(result.Single(s => s.Id == RemoteServiceId.Rdp).IsEnabled);
        Assert.Contains(logs, l => l.Level == LogLevel.Error && l.Message.Contains("sshd"));
        Assert.Contains(logs, l => l.Level == LogLevel.Warning && l.Message.Contains("ошибками"));
    }
}
