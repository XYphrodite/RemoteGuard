using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using RemoteGuard.App.Services;
using SelfUpdateKit;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace RemoteGuard.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        if (args.Any(a => a is "-h" or "-?" || a.Equals("--help", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"RemoteGuard {RemoteGuardUpdate.DisplayVersion}");
            Console.WriteLine("Usage: RemoteGuard [-Tray] [--version] [--update [--check]]");
            Environment.ExitCode = 0;
            Shutdown();
            return;
        }

        if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(RemoteGuardUpdate.InstalledVersion.ToString());
            Environment.ExitCode = 0;
            Shutdown();
            return;
        }

        if (args.Any(a => a.Equals("--apply-update", StringComparison.OrdinalIgnoreCase))
            || args.Any(a => a.Equals("--update", StringComparison.OrdinalIgnoreCase)))
        {
            var checkOnly = args.Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase));
            var code = Task.Run(() => RunCliUpdateAsync(checkOnly)).GetAwaiter().GetResult();
            Environment.ExitCode = code;
            Shutdown();
            return;
        }

        var tray = args.Contains("-Tray", StringComparer.OrdinalIgnoreCase);

        RemoteGuardUpdate.CleanUpRetiredCopies();

        _mutex = new Mutex(false, @"Local\RemoteGuard.UI");
        try { _ownsMutex = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }

        if (!_ownsMutex)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(@"Local\RemoteGuard.Show");
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                MessageBox.Show(
                    "Remote Guard уже запущен. Нажми на значок щита в трее возле часов.",
                    "Remote Guard",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\RemoteGuard.Show");

        var window = new MainWindow(() => _showEvent, startHidden: tray);
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
        if (!tray) window.Show();
    }

    public void RestartIntoUpdatedBinary(bool startInTray)
    {
        var exe = Environment.ProcessPath;
        ReleaseSingleInstanceHandles();

        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
        {
            var startInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            };
            if (startInTray)
                startInfo.Arguments = "-Tray";
            Process.Start(startInfo);
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstanceHandles();
        base.OnExit(e);
    }

    private void ReleaseSingleInstanceHandles()
    {
        _showEvent?.Dispose();
        _showEvent = null;
        if (_ownsMutex && _mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* never owned */ }
            _ownsMutex = false;
        }
        _mutex?.Dispose();
        _mutex = null;
    }

    private static async Task<int> RunCliUpdateAsync(bool checkOnly)
    {
        try
        {
            if (checkOnly)
            {
                var check = await RemoteGuardUpdate.CheckAsync(CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine(check.Status == SelfUpdateStatus.UpdateAvailable
                    ? $"Update available: {check.Installed} -> {check.Release} ({check.Tag})"
                    : $"RemoteGuard {check.Installed} is up to date.");
                return check.Status == SelfUpdateStatus.UpdateAvailable ? 1 : 0;
            }

            var report = await RemoteGuardUpdate.ApplyInProcessAsync(null, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine(report.Status == SelfUpdateStatus.Updated
                ? $"Updated to {report.Tag}."
                : $"RemoteGuard {report.Installed} is up to date.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Update failed: {ex.Message}");
            return 2;
        }
    }
}
