using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteGuard.App.Services;

namespace RemoteGuard.App;

/// <summary>
/// System tray icon with color-coded shield badge and quick toggle actions.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _disableAllItem;
    private readonly ToolStripMenuItem _enableAllItem;
    private readonly Icon _openIcon;
    private readonly Icon _partialIcon;
    private readonly Icon _blockedIcon;
    private OverallAccessState _lastIconState = OverallAccessState.Unknown;

    public event Action? OpenRequested;
    public event Action? DisableAllRequested;
    public event Action? EnableAllRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _openIcon = CreateShieldIcon(Color.FromArgb(255, 97, 232, 186));    // Mint: remote access open
        _partialIcon = CreateShieldIcon(Color.FromArgb(255, 255, 209, 102)); // Amber: partial
        _blockedIcon = CreateShieldIcon(Color.FromArgb(255, 255, 122, 112)); // Coral-red: all blocked

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Открыть Remote Guard");
        _disableAllItem = new ToolStripMenuItem("Заблокировать весь доступ");
        _enableAllItem = new ToolStripMenuItem("Включить весь доступ");
        var exit = new ToolStripMenuItem("Закрыть приложение");

        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_disableAllItem);
        menu.Items.Add(_enableAllItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        open.Click += (_, _) => OpenRequested?.Invoke();
        _disableAllItem.Click += (_, _) => DisableAllRequested?.Invoke();
        _enableAllItem.Click += (_, _) => EnableAllRequested?.Invoke();
        exit.Click += (_, _) => ExitRequested?.Invoke();

        _icon = new NotifyIcon
        {
            Icon = _openIcon,
            Text = "Remote Guard — проверка служб…",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void UpdateState(OverallAccessState state, int activeCount, int installedCount, bool busy)
    {
        if (state != _lastIconState)
        {
            _icon.Icon = state switch
            {
                OverallAccessState.AllBlocked => _blockedIcon,
                OverallAccessState.Partial => _partialIcon,
                _ => _openIcon,
            };
            _lastIconState = state;
        }

        var tooltip = busy
            ? "Remote Guard — выполняем команду…"
            : state switch
            {
                OverallAccessState.AllBlocked => "Remote Guard — доступ заблокирован (0 активных)",
                OverallAccessState.AllEnabled => $"Remote Guard — доступ открыт ({activeCount}/{installedCount})",
                OverallAccessState.Partial => $"Remote Guard — частично открыт ({activeCount}/{installedCount})",
                _ => "Remote Guard — контроль удалённого доступа",
            };

        _icon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
        _disableAllItem.Enabled = !busy && activeCount > 0;
        _enableAllItem.Enabled = !busy && installedCount > 0 && activeCount < installedCount;
    }

    private static Icon CreateShieldIcon(Color shieldColor)
    {
        const int sz = 32;
        using var bmp = new Bitmap(sz, sz);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bg = new SolidBrush(Color.FromArgb(255, 16, 20, 29));
        using var bgPath = CreateRoundedRect(1, 1, sz - 2, sz - 2, 7);
        g.FillPath(bg, bgPath);

        using var shieldBrush = new SolidBrush(shieldColor);
        using var shield = new GraphicsPath();
        shield.AddPolygon([
            new PointF(sz * 0.5f, sz * 0.16f),
            new PointF(sz * 0.78f, sz * 0.28f),
            new PointF(sz * 0.78f, sz * 0.52f),
            new PointF(sz * 0.5f, sz * 0.85f),
            new PointF(sz * 0.22f, sz * 0.52f),
            new PointF(sz * 0.22f, sz * 0.28f),
        ]);
        g.FillPath(shieldBrush, shield);

        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _openIcon.Dispose();
        _partialIcon.Dispose();
        _blockedIcon.Dispose();
    }
}
