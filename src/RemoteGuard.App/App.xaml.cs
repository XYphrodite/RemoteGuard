using System.Threading;
using System.Windows;
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
            Console.WriteLine("RemoteGuard 1.0.0");
            Console.WriteLine("Usage: RemoteGuard [-Tray]");
            Environment.ExitCode = 0;
            Shutdown();
            return;
        }

        var tray = args.Contains("-Tray", StringComparer.OrdinalIgnoreCase);

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

    protected override void OnExit(ExitEventArgs e)
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
        base.OnExit(e);
    }
}
