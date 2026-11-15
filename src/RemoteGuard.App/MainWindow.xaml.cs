using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RemoteGuard.App.Services;
using SelfUpdateKit;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;

namespace RemoteGuard.App;

public partial class MainWindow : Window
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x61, 0xE8, 0xBA));
    private static readonly Brush AccentDark = new SolidColorBrush(Color.FromRgb(0x10, 0x25, 0x1E));
    private static readonly Brush SmallSurface = new SolidColorBrush(Color.FromRgb(0x2B, 0x38, 0x4E));
    private static readonly Brush LightText = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xFA));
    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0xAB, 0xC4, 0xF7));
    private static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66));
    private static readonly Brush Blocked = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x92, 0x9F, 0xB4));
    private static readonly Brush NotInstalled = new SolidColorBrush(Color.FromRgb(0x5C, 0x67, 0x7D));

    private const int MaxLogLines = 300;

    private readonly Func<EventWaitHandle?>? _showEventAccessor;
    private readonly TrayIcon _tray = new();
    private readonly RemoteAccessCoordinator _coordinator;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _signalTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly Queue<string> _logLines = new();

    private IReadOnlyList<ServiceStatusSnapshot> _snapshots = [];
    private SelfUpdateReport? _availableUpdate;
    private bool _busy;
    private bool _pollBusy;
    private bool _updateBusy;
    private bool _updatingUi;
    private bool _exitRequested;

    public MainWindow(Func<EventWaitHandle?>? showEventAccessor, bool startHidden)
    {
        _showEventAccessor = showEventAccessor;
        _coordinator = new RemoteAccessCoordinator(new WindowsSystemController());

        InitializeComponent();
        WindowFrame.Hook(this);
        UpdateMaximizeGlyph();

        var displayVersion = RemoteGuardUpdate.DisplayVersion;
        Title = $"Remote Guard {displayVersion}";
        TopBarTitleLabel.Text = Title;
        VersionLabel.Text = displayVersion;

        var isAdmin = _coordinator.IsAdministrator;
        ComputerLabel.Text = isAdmin
            ? $"{Environment.MachineName}  /  ПРАВА АДМИНИСТРАТОРА"
            : $"{Environment.MachineName}  /  НЕТ ПРАВ АДМИНИСТРАТОРА (ЗАПУСТИ ОТ ИМЕНИ АДМИНИСТРАТОРА)";
        ComputerLabel.Foreground = isAdmin ? Muted : Warning;

        _coordinator.LogAdded += OnCoordinatorLogAdded;

        _tray.OpenRequested += ShowWindow;
        _tray.DisableAllRequested += () => _ = ToggleAllAsync(explicitEnable: false);
        _tray.EnableAllRequested += () => _ = ToggleAllAsync(explicitEnable: true);
        _tray.UpdateRequested += () => _ = HandleUpdateActionAsync(userInitiated: true);
        _tray.ExitRequested += ExitApplication;

        _pollTimer.Tick += (_, _) => _ = RefreshStatusAsync(userInitiated: false);
        _signalTimer.Tick += (_, _) =>
        {
            if (_showEventAccessor?.Invoke() is { } ev && ev.WaitOne(0))
                ShowWindow();
        };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync(userInitiated: false);

        _pollTimer.Start();
        _signalTimer.Start();
        _updateTimer.Start();

        AppendLog(new LogEntry(DateTimeOffset.Now, isAdmin ? LogLevel.Info : LogLevel.Warning,
            isAdmin
                ? "Remote Guard запущен с правами администратора."
                : "Запущено без прав администратора — переключение служб может не сработать."));

        _ = RefreshStatusAsync(userInitiated: false);
        _ = CheckForUpdateStartupAsync();

        if (startHidden)
            Hide();
    }

    private void OnCoordinatorLogAdded(LogEntry entry)
    {
        if (Dispatcher.CheckAccess())
            AppendLog(entry);
        else
            Dispatcher.BeginInvoke(() => AppendLog(entry));
    }

    private void AppendLog(LogEntry entry)
    {
        _logLines.Enqueue(entry.FormatLine());
        while (_logLines.Count > MaxLogLines)
            _logLines.Dequeue();

        var sb = new StringBuilder();
        foreach (var line in _logLines)
            sb.AppendLine(line);

        LogTextBox.Text = sb.ToString();
        LogTextBox.ScrollToEnd();
    }

    private async Task RefreshStatusAsync(bool userInitiated)
    {
        if (_pollBusy || _busy) return;
        _pollBusy = true;
        try
        {
            if (userInitiated)
                AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Info, "Обновляем статус служб…"));

            var snapshots = await _coordinator.RefreshAllAsync();
            _snapshots = snapshots;
            ApplySnapshotsToUi(snapshots);
        }
        catch (Exception ex)
        {
            AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Error, $"Ошибка опроса служб: {ex.Message}"));
        }
        finally
        {
            _pollBusy = false;
        }
    }

    private async Task ToggleAllAsync(bool? explicitEnable = null)
    {
        if (_busy) return;
        _busy = true;
        ApplyBusyState(true);

        try
        {
            if (_snapshots.Count == 0)
                _snapshots = await _coordinator.RefreshAllAsync();

            var overall = RemoteAccessCoordinator.ComputeOverallState(_snapshots);
            var enable = explicitEnable ?? (overall == OverallAccessState.AllBlocked);

            var updated = await _coordinator.SetAllEnabledAsync(enable);
            _snapshots = updated;
            ApplySnapshotsToUi(updated);
        }
        catch (Exception ex)
        {
            AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Error, $"Сбой переключения: {ex.Message}"));
        }
        finally
        {
            _busy = false;
            ApplyBusyState(false);
            if (_snapshots.Count > 0)
                ApplySnapshotsToUi(_snapshots);
        }
    }

    private async Task ToggleSingleAsync(RemoteServiceId id, bool enable)
    {
        if (_busy) return;
        _busy = true;
        ApplyBusyState(true);

        try
        {
            var updated = await _coordinator.SetServiceEnabledAsync(id, enable);
            _snapshots = updated;
            ApplySnapshotsToUi(updated);
        }
        catch (Exception ex)
        {
            AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Error, $"Ошибка управления {id}: {ex.Message}"));
        }
        finally
        {
            _busy = false;
            ApplyBusyState(false);
            if (_snapshots.Count > 0)
                ApplySnapshotsToUi(_snapshots);
        }
    }

    private void ApplyBusyState(bool busy)
    {
        PowerButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        TailscaleToggle.IsEnabled = !busy;
        SshToggle.IsEnabled = !busy;
        RdpToggle.IsEnabled = !busy;
        AnyDeskToggle.IsEnabled = !busy;

        if (busy)
        {
            PowerLabel.Text = "Применяем…";
            PowerSubLabel.Text = "Подожди несколько секунд";
            var installed = _snapshots.Count(s => s.IsInstalled);
            var active = _snapshots.Count(s => s.IsInstalled && s.IsEnabled);
            _tray.UpdateState(RemoteAccessCoordinator.ComputeOverallState(_snapshots), active, installed, busy: true);
        }
    }

    private void ApplySnapshotsToUi(IReadOnlyList<ServiceStatusSnapshot> snapshots)
    {
        _updatingUi = true;
        try
        {
            foreach (var snap in snapshots)
            {
                var (dot, detail, toggle) = GetRowControls(snap.Id);
                detail.Text = snap.DetailText;
                toggle.IsChecked = snap.IsEnabled;
                toggle.IsEnabled = !_busy && snap.IsInstalled;

                if (!snap.IsInstalled)
                {
                    dot.Fill = NotInstalled;
                    detail.Foreground = Muted;
                }
                else if (snap.IsEnabled)
                {
                    dot.Fill = Accent;
                    detail.Foreground = Muted;
                }
                else
                {
                    dot.Fill = Blocked;
                    detail.Foreground = Muted;
                }
            }

            var installed = snapshots.Where(s => s.IsInstalled).ToList();
            var active = installed.Where(s => s.IsEnabled).ToList();
            var overall = RemoteAccessCoordinator.ComputeOverallState(snapshots);

            RefreshButton.IsEnabled = !_busy;
            PowerButton.IsEnabled = !_busy && installed.Count > 0;

            switch (overall)
            {
                case OverallAccessState.AllBlocked:
                    OverallStatusLabel.Text = "Доступ заблокирован";
                    OverallStatusLabel.Foreground = Blocked;
                    OverallDetailLabel.Text = $"Безопасно для стрима и игры · активно {active.Count} из {installed.Count}";
                    PowerButton.Background = Idle;
                    PowerButton.Foreground = AccentDark;
                    PowerIcon.Text = "\uE785"; // Unlock
                    PowerLabel.Text = "Включить всё";
                    PowerSubLabel.Text = "Запустит службы удалённого доступа";
                    break;

                case OverallAccessState.Partial:
                    OverallStatusLabel.Text = "Частичный доступ";
                    OverallStatusLabel.Foreground = Warning;
                    OverallDetailLabel.Text = $"Активно {active.Count} из {installed.Count}: {string.Join(", ", active.Select(a => ShortName(a.Id)))}";
                    PowerButton.Background = Accent;
                    PowerButton.Foreground = AccentDark;
                    PowerIcon.Text = "\uE72E"; // Lock
                    PowerLabel.Text = "Выключить всё";
                    PowerSubLabel.Text = "Остановит активные службы и закроет AnyDesk";
                    break;

                case OverallAccessState.AllEnabled:
                    OverallStatusLabel.Text = "Доступ открыт";
                    OverallStatusLabel.Foreground = Accent;
                    OverallDetailLabel.Text = $"Работают все службы ({active.Count} из {installed.Count})";
                    PowerButton.Background = Accent;
                    PowerButton.Foreground = AccentDark;
                    PowerIcon.Text = "\uE72E"; // Lock
                    PowerLabel.Text = "Выключить всё";
                    PowerSubLabel.Text = "Остановит службы и закроет AnyDesk";
                    break;

                default:
                    OverallStatusLabel.Text = "Службы не найдены";
                    OverallStatusLabel.Foreground = Muted;
                    OverallDetailLabel.Text = "Ни одна из отслеживаемых служб не обнаружена.";
                    PowerButton.Background = Idle;
                    PowerButton.Foreground = AccentDark;
                    PowerIcon.Text = "\uE7E8";
                    PowerLabel.Text = "Недоступно";
                    PowerSubLabel.Text = "";
                    break;
            }

            _tray.UpdateState(overall, active.Count, installed.Count, _busy);
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private (Ellipse Dot, TextBlock Detail, CheckBox Toggle) GetRowControls(RemoteServiceId id) => id switch
    {
        RemoteServiceId.Tailscale => (TailscaleDot, TailscaleDetail, TailscaleToggle),
        RemoteServiceId.Ssh => (SshDot, SshDetail, SshToggle),
        RemoteServiceId.Rdp => (RdpDot, RdpDetail, RdpToggle),
        RemoteServiceId.AnyDesk => (AnyDeskDot, AnyDeskDetail, AnyDeskToggle),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    private static string ShortName(RemoteServiceId id) => id switch
    {
        RemoteServiceId.Tailscale => "Tailscale",
        RemoteServiceId.Ssh => "SSH",
        RemoteServiceId.Rdp => "RDP",
        RemoteServiceId.AnyDesk => "AnyDesk",
        _ => id.ToString(),
    };

    // ---- UI events -------------------------------------------------------

    private void OnPowerClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _ = ToggleAllAsync();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _ = RefreshStatusAsync(userInitiated: true);
    }

    private void OnServiceToggleClick(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _busy) return;
        if (sender is not CheckBox cb || cb.Tag is not string tag) return;
        if (!Enum.TryParse<RemoteServiceId>(tag, out var id)) return;

        var targetEnabled = cb.IsChecked == true;
        _ = ToggleSingleAsync(id, targetEnabled);
    }

    private void OnCopyLogsClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(LogTextBox.Text))
            Clipboard.SetText(LogTextBox.Text);
    }

    private void OnClearLogsClick(object sender, RoutedEventArgs e)
    {
        _logLines.Clear();
        LogTextBox.Clear();
    }

    // ---- self-update (SelfUpdateKit) -------------------------------------

    private void OnUpdateClick(object sender, RoutedEventArgs e) =>
        _ = HandleUpdateActionAsync(userInitiated: true);

    private async Task CheckForUpdateStartupAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        await CheckForUpdateAsync(userInitiated: false);
    }

    private Task HandleUpdateActionAsync(bool userInitiated) =>
        _availableUpdate is { Status: SelfUpdateStatus.UpdateAvailable }
            ? ApplyUpdateAsync()
            : CheckForUpdateAsync(userInitiated);

    private async Task CheckForUpdateAsync(bool userInitiated)
    {
        if (_updateBusy)
            return;

        if (!RemoteGuardUpdate.CanSelfUpdate(Environment.ProcessPath))
        {
            if (userInitiated)
            {
                UpdateStatusLabel.Foreground = Muted;
                UpdateStatusLabel.Text = "Доступно только в установленном RemoteGuard.exe";
            }
            return;
        }

        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        _tray.SetUpdateState("Проверяем обновления…", enabled: false);
        if (userInitiated)
        {
            UpdateStatusLabel.Foreground = Muted;
            UpdateStatusLabel.Text = "Проверяем GitHub Releases…";
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var report = await RemoteGuardUpdate.CheckAsync(cts.Token);
            if (report.Status == SelfUpdateStatus.UpdateAvailable)
            {
                _availableUpdate = report;
                var label = $"Обновить до v{report.Release}";
                UpdateButton.Content = label;
                UpdateButton.Background = Accent;
                UpdateButton.Foreground = AccentDark;
                UpdateStatusLabel.Foreground = Accent;
                UpdateStatusLabel.Text = $"Доступна новая версия {report.Tag}";
                _tray.SetUpdateState(label, enabled: true);
                AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Info, $"Доступно обновление: {report.Tag}"));
            }
            else
            {
                _availableUpdate = null;
                ResetUpdateButtonAppearance();
                _tray.SetUpdateState("Проверить обновления", enabled: true);
                if (userInitiated)
                {
                    UpdateStatusLabel.Foreground = Muted;
                    UpdateStatusLabel.Text = $"Актуальная версия ({RemoteGuardUpdate.DisplayVersion})";
                    AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Info, $"Обновлений нет ({RemoteGuardUpdate.DisplayVersion})."));
                }
            }
        }
        catch (Exception ex)
        {
            _tray.SetUpdateState(
                _availableUpdate is { Status: SelfUpdateStatus.UpdateAvailable } avail
                    ? $"Обновить до v{avail.Release}"
                    : "Проверить обновления",
                enabled: true);
            if (userInitiated)
            {
                UpdateStatusLabel.Foreground = Blocked;
                UpdateStatusLabel.Text = "Не удалось проверить: " + ex.Message;
                AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Warning, $"Ошибка проверки обновлений: {ex.Message}"));
            }
        }
        finally
        {
            _updateBusy = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private async Task ApplyUpdateAsync()
    {
        if (_updateBusy) return;
        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        _tray.SetUpdateState("Обновляем…", enabled: false);
        UpdateStatusLabel.Foreground = Muted;
        UpdateStatusLabel.Text = "Скачивание обновления…";
        AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Info, "Скачиваем обновление через SelfUpdateKit…"));

        try
        {
            var target = _availableUpdate;
            var report = await RemoteGuardUpdate.ApplyWithElevationFallbackAsync(
                target?.Release,
                target?.Tag,
                progress => Dispatcher.BeginInvoke(() => ShowUpdateProgress(progress)),
                CancellationToken.None);

            if (report.Status == SelfUpdateStatus.Updated)
            {
                UpdateStatusLabel.Foreground = Accent;
                UpdateStatusLabel.Text = $"Установлена {report.Tag}. Перезапуск…";
                AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Success, $"Обновлено до {report.Tag}. Перезапуск…"));
                var startInTray = !IsVisible;
                _exitRequested = true;
                Close();
                if (Application.Current is App app)
                    app.RestartIntoUpdatedBinary(startInTray);
                return;
            }

            _availableUpdate = null;
            ResetUpdateButtonAppearance();
            _tray.SetUpdateState("Проверить обновления", enabled: true);
            UpdateStatusLabel.Foreground = Muted;
            UpdateStatusLabel.Text = $"Актуальная версия ({RemoteGuardUpdate.DisplayVersion})";
        }
        catch (Exception ex)
        {
            UpdateStatusLabel.Foreground = Blocked;
            UpdateStatusLabel.Text = "Ошибка обновления: " + ex.Message;
            AppendLog(new LogEntry(DateTimeOffset.Now, LogLevel.Error, $"Ошибка обновления: {ex.Message}"));
            _tray.SetUpdateState(
                _availableUpdate is { } avail ? $"Обновить до v{avail.Release}" : "Проверить обновления",
                enabled: true);
        }
        finally
        {
            _updateBusy = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private void ShowUpdateProgress(SelfUpdateProgress progress)
    {
        UpdateStatusLabel.Foreground = Muted;
        UpdateStatusLabel.Text = progress.Phase switch
        {
            SelfUpdatePhase.Checking => "Проверка релиза…",
            SelfUpdatePhase.Downloading when progress.TotalBytes is > 0 && progress.ReceivedBytes is { } r =>
                $"Скачивание {Math.Min(100, (int)(100.0 * r / progress.TotalBytes.Value))}%…",
            SelfUpdatePhase.Downloading => "Скачивание…",
            SelfUpdatePhase.Verifying => "Проверка SHA-256…",
            SelfUpdatePhase.Installing => "Установка и проверка запуска…",
            _ => UpdateStatusLabel.Text,
        };
    }

    private void ResetUpdateButtonAppearance()
    {
        UpdateButton.Content = "Проверить обновления";
        UpdateButton.Background = SmallSurface;
        UpdateButton.Foreground = LightText;
    }

    // ---- Window / Tray lifecycle -----------------------------------------

    private void ShowWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer.Stop();
        _signalTimer.Stop();
        _updateTimer.Stop();
        _coordinator.LogAdded -= OnCoordinatorLogAdded;
        _tray.Dispose();
        base.OnClosed(e);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateMaximizeGlyph();

    private void UpdateMaximizeGlyph()
    {
        var max = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
        MaximizeButton.ToolTip = max ? "Восстановить" : "Развернуть";
        OuterFrame.BorderThickness = max ? new Thickness(0) : new Thickness(1);
    }
}
