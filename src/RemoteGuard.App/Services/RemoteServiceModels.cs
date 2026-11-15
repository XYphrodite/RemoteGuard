namespace RemoteGuard.App.Services;

public enum RemoteServiceId
{
    Tailscale,
    Ssh,
    Rdp,
    AnyDesk,
}

public enum OverallAccessState
{
    Unknown,
    AllBlocked,
    Partial,
    AllEnabled,
}

public sealed record RemoteServiceDefinition(
    RemoteServiceId Id,
    string DisplayName,
    string Subtitle,
    string ServiceName,
    string[] ProcessNames,
    bool UsesTailscaleCli = false)
{
    public static IReadOnlyList<RemoteServiceDefinition> Defaults { get; } =
    [
        new(
            RemoteServiceId.Tailscale,
            "Tailscale VPN",
            "Служба Tailscale · tailscale down / up",
            "Tailscale",
            [],
            UsesTailscaleCli: true),
        new(
            RemoteServiceId.Ssh,
            "OpenSSH (SSH)",
            "Служба sshd · порт 22",
            "sshd",
            []),
        new(
            RemoteServiceId.Rdp,
            "Удалённый рабочий стол (RDP)",
            "Служба TermService · порт 3389",
            "TermService",
            []),
        new(
            RemoteServiceId.AnyDesk,
            "AnyDesk",
            "Служба AnyDesk + процесс AnyDesk.exe",
            "AnyDesk",
            ["AnyDesk"]),
    ];
}

public sealed record ServiceProbeResult(
    bool ServiceExists,
    bool ServiceRunning,
    bool ServicePending,
    string? StartMode,
    int RunningProcessCount);

public sealed record CliCommandResult(
    int ExitCode,
    string StdOut,
    string StdErr);

public sealed record ServiceStatusSnapshot(
    RemoteServiceDefinition Definition,
    bool IsInstalled,
    bool ServiceRunning,
    bool ProcessRunning,
    bool? TailscaleConnected,
    bool IsEnabled,
    bool IsDisabledStartMode,
    string DetailText)
{
    public RemoteServiceId Id => Definition.Id;
}

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message)
{
    public string FormatLine()
    {
        var tag = Level switch
        {
            LogLevel.Success => "OK  ",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERR ",
            _ => "INFO",
        };
        return $"[{Timestamp:HH:mm:ss}] [{tag}] {Message}";
    }
}
