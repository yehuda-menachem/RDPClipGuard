using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RDPClipGuard;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ClipboardMonitor _monitor;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _copyCountItem;
    private readonly ToolStripMenuItem _startupItem;
    private string _lastStatus = "Starting...";

    private const string RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "RDPClipGuard";

    public TrayApplicationContext()
    {
        _monitor = new ClipboardMonitor();
        _monitor.StatusChanged += OnStatusChanged;

        _statusItem = new ToolStripMenuItem("Status: Starting...")
        {
            Enabled = false
        };

        _copyCountItem = new ToolStripMenuItem("Copies: 0")
        {
            Enabled = false
        };

        _startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = true
        };
        _startupItem.Click += OnToggleStartup;

        // Auto-enable startup on first run
        if (!IsStartupEnabled())
        {
            SetStartup(true);
            _startupItem.Checked = true;
        }

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(new ToolStripMenuItem("RDPClipGuard v1.0") { Enabled = false, Font = new Font(contextMenu.Font, FontStyle.Bold) });
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_statusItem);
        contextMenu.Items.Add(_copyCountItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Reset rdpclip Now", null, OnResetNow);
        contextMenu.Items.Add(_startupItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, OnExit);

        _trayIcon = new NotifyIcon
        {
            Icon = CreateDefaultIcon(),
            Text = "RDPClipGuard - Monitoring clipboard",
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) =>
        {
            _trayIcon.ShowBalloonTip(3000, "RDPClipGuard",
                $"Copies: {_monitor.CopyCount}\n{_lastStatus}",
                ToolTipIcon.Info);
        };

        _monitor.Start();
    }

    private void OnStatusChanged(string message)
    {
        _lastStatus = message;

        if (_trayIcon.IsHandleCreated() || true)
        {
            try
            {
                if (_statusItem.GetCurrentParent()?.InvokeRequired == true)
                {
                    _statusItem.GetCurrentParent()?.BeginInvoke(() => UpdateUI(message));
                }
                else
                {
                    UpdateUI(message);
                }
            }
            catch
            {
                // UI might be disposing
            }
        }
    }

    private void UpdateUI(string message)
    {
        var shortMessage = message.Length > 60 ? message[..60] + "..." : message;
        _statusItem.Text = $"Status: {shortMessage}";
        _copyCountItem.Text = $"Copies: {_monitor.CopyCount}";
        _trayIcon.Text = $"RDPClipGuard - {_monitor.CopyCount} copies";
    }

    private void OnResetNow(object? sender, EventArgs e)
    {
        Task.Run(() =>
        {
            _trayIcon.ShowBalloonTip(2000, "RDPClipGuard", "Resetting rdpclip...", ToolTipIcon.Info);
            ResetRdpClipManual();
            _trayIcon.ShowBalloonTip(2000, "RDPClipGuard", "rdpclip has been reset.", ToolTipIcon.Info);
        });
    }

    private static void ResetRdpClipManual()
    {
        try
        {
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("rdpclip"))
            {
                try { proc.Kill(); } catch { }
            }
            Thread.Sleep(500);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rdpclip.exe",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }

    private void OnToggleStartup(object? sender, EventArgs e)
    {
        SetStartup(_startupItem.Checked);
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, false);
        return key?.GetValue(AppName) != null;
    }

    private static void SetStartup(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, true);
        if (key == null) return;

        if (enable)
        {
            var exePath = Application.ExecutablePath;
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _monitor.Stop();
        _monitor.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }

    private static Icon CreateDefaultIcon()
    {
        // Create a simple clipboard-themed icon programmatically
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Clipboard body
            using var bodyBrush = new SolidBrush(Color.FromArgb(70, 130, 180));
            g.FillRoundedRectangle(bodyBrush, 4, 6, 24, 24, 3);

            // Clipboard clip
            using var clipPen = new Pen(Color.FromArgb(50, 100, 150), 2);
            g.DrawRoundedRectangle(clipPen, 10, 2, 12, 8, 2);

            // Lines on clipboard
            using var linePen = new Pen(Color.White, 2);
            g.DrawLine(linePen, 9, 16, 23, 16);
            g.DrawLine(linePen, 9, 21, 23, 21);
            g.DrawLine(linePen, 9, 26, 18, 26);
        }

        var handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.Dispose();
            _trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}

public static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, float x, float y, float w, float h, float r)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics g, Pen pen, float x, float y, float w, float h, float r)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }

    public static bool IsHandleCreated(this NotifyIcon icon)
    {
        // NotifyIcon doesn't expose handle directly, this is a helper
        return true;
    }
}
