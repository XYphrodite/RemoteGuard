using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using SelfUpdateKit;

namespace RemoteGuard.App.Services;

/// <summary>
/// RemoteGuard wiring for the shared SelfUpdateKit: fixed GitHub repository, single-file
/// executable asset + mandatory SHA-256 sidecar, direct download URLs (no 60 req/hr API cap),
/// and UAC fallback if installed in a protected directory.
/// </summary>
public static class RemoteGuardUpdate
{
    public const string Repository = "XYphrodite/RemoteGuard";
    public const string ExecutableAsset = "RemoteGuard.exe";
    public const string ChecksumAsset = "RemoteGuard.exe.sha256";
    public const string StagingPrefix = ".remoteguard-update-";

    public static ReleaseVersion InstalledVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version
                ?? typeof(RemoteGuardUpdate).Assembly.GetName().Version;
            return v is null
                ? new ReleaseVersion(0, 0, 0)
                : new ReleaseVersion(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string DisplayVersion => $"v{InstalledVersion}";

    public static ReleaseSourceOptions Options() => new()
    {
        Repository = Repository,
        ExecutableAssetNames = { ExecutableAsset },
        ChecksumAssetName = ChecksumAsset,
        ChecksumMode = ChecksumMode.SidecarAsset,
        UseDirectDownloadUrls = true,
        UserAgent = $"RemoteGuard/{InstalledVersion}",
        StagingPrefix = StagingPrefix,
    };

    public static bool CanSelfUpdate(string? processPath) =>
        !string.IsNullOrWhiteSpace(processPath)
        && Path.IsPathFullyQualified(processPath)
        && Path.GetFileName(processPath).Equals(ExecutableAsset, StringComparison.OrdinalIgnoreCase)
        && File.Exists(processPath);

    public static void CleanUpRetiredCopies(string? processPath = null)
    {
        var path = processPath ?? Environment.ProcessPath;
        if (!CanSelfUpdate(path)) return;
        try
        {
            new ExecutableReplacer().RemoveRetiredCopies(Path.GetFullPath(path!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static async Task<SelfUpdateReport> CheckAsync(CancellationToken ct)
    {
        var path = RequireExecutablePath();
        var options = Options();
        using var source = new GitHubReleaseSource(options);
        var service = new SelfUpdateService(path, InstalledVersion, source, options);
        return await service.CheckAsync(ct).ConfigureAwait(false);
    }

    public static async Task<SelfUpdateReport> ApplyInProcessAsync(
        Action<SelfUpdateProgress>? progress,
        CancellationToken ct)
    {
        var path = RequireExecutablePath();
        var options = Options();
        using var source = new GitHubReleaseSource(options);
        var service = new SelfUpdateService(path, InstalledVersion, source, options);
        return await service.UpdateAsync(new SelfUpdateRequest(), ct, progress).ConfigureAwait(false);
    }

    public static async Task<SelfUpdateReport> ApplyWithElevationFallbackAsync(
        ReleaseVersion? expectedRelease,
        string? expectedTag,
        Action<SelfUpdateProgress>? progress,
        CancellationToken ct)
    {
        var path = RequireExecutablePath();
        var dir = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Не удалось определить папку установки.");

        if (IsDirectoryWritable(dir))
            return await ApplyInProcessAsync(progress, ct).ConfigureAwait(false);

        progress?.Invoke(new SelfUpdateProgress(SelfUpdatePhase.Installing));
        var startInfo = new ProcessStartInfo(path)
        {
            Arguments = "--apply-update",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = dir,
        };

        Process? elevated;
        try
        {
            elevated = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Обновление отменено (требовались права администратора).");
        }

        if (elevated is null)
            throw new InvalidOperationException("Не удалось запустить процесс обновления.");

        using (elevated)
        {
            await elevated.WaitForExitAsync(ct).ConfigureAwait(false);
            if (elevated.ExitCode != 0)
                throw new InvalidOperationException($"Процесс обновления завершился с кодом {elevated.ExitCode}.");
        }

        return new SelfUpdateReport(
            SelfUpdateStatus.Updated,
            InstalledVersion,
            expectedRelease ?? InstalledVersion,
            expectedTag ?? DisplayVersion);
    }

    private static string RequireExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!CanSelfUpdate(path))
            throw new InvalidOperationException("Самообновление доступно только для установленного RemoteGuard.exe.");
        return Path.GetFullPath(path!);
    }

    private static bool IsDirectoryWritable(string directory)
    {
        var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
