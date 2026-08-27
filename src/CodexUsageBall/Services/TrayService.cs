using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace CodexUsageBall.Services;

public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;

    public TrayService()
    {
        _icon = CreateIcon();
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "Codex 用量",
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = Color.FromArgb(31, 31, 30),
            ForeColor = Color.FromArgb(244, 244, 239),
            Font = new Font("Microsoft YaHei UI", 10f)
        };
        menu.Items.Add(CreateItem("显示悬浮球", (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(CreateItem("设置", (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(CreateItem("退出", (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public void UpdateStatus(string remaining)
    {
        var text = $"Codex 剩余 {remaining}";
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private static WinForms.ToolStripMenuItem CreateItem(string text, EventHandler onClick)
    {
        var item = new WinForms.ToolStripMenuItem(text)
        {
            AutoSize = true,
            Padding = new WinForms.Padding(4, 5, 18, 5)
        };
        item.Click += onClick;
        return item;
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(26, 26, 25));
        using var track = new Pen(Color.FromArgb(72, 72, 68), 6f);
        using var foreground = new Pen(Color.FromArgb(185, 245, 200), 6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.FillEllipse(background, 3, 3, 58, 58);
        graphics.DrawEllipse(track, 11, 11, 42, 42);
        graphics.DrawArc(foreground, 11, 11, 42, 42, -90, 286);
        using var centerBrush = new SolidBrush(Color.FromArgb(245, 245, 240));
        using var font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = graphics.MeasureString("C", font);
        graphics.DrawString("C", font, centerBrush, 32 - size.Width / 2f, 32 - size.Height / 2f - 1f);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
