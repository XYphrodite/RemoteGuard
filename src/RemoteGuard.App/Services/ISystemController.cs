namespace RemoteGuard.App.Services;

/// <summary>
/// Abstracts Windows service control, process termination, and Tailscale CLI calls
/// so coordination logic can be tested without touching host services.
/// </summary>
public interface ISystemController
{
    bool IsAdministrator();

    Task<ServiceProbeResult> ProbeServiceAsync(
        string serviceName,
        IReadOnlyList<string> processNames,
        CancellationToken ct);

    Task StopServiceWithDependentsAsync(
        string serviceName,
        TimeSpan timeout,
        Action<string> log,
        CancellationToken ct);

    Task StartServiceAsync(
        string serviceName,
        TimeSpan timeout,
        Action<string> log,
        CancellationToken ct);

    Task<int> KillProcessesAsync(
        IReadOnlyList<string> processNames,
        Action<string> log,
        CancellationToken ct);

    Task<string?> FindTailscaleCliPathAsync(CancellationToken ct);

    Task<CliCommandResult> RunProcessAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken ct);
}
