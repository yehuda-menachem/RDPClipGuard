using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
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
    private readonly ToolStripMenuItem _diagnosticItem;
    private string _lastStatus = "Starting...";
    private readonly bool _isRemoteSession;
    private readonly bool _isShadowSession;

    private const string RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "RDPClipGuard";

    public TrayApplicationContext()
    {
        // Detect if running in RDP session
        _isRemoteSession = SystemInformation.TerminalServerSession;

        // Shadow session = local machine (not RDP) but rdpclip is running (viewing a remote session)
        var rdpClipProcs = System.Diagnostics.Process.GetProcessesByName("rdpclip");
        _isShadowSession = !_isRemoteSession && rdpClipProcs.Length > 0;
        foreach (var p in rdpClipProcs) p.Dispose();

        _monitor = new ClipboardMonitor(_isShadowSession);
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

        _diagnosticItem = new ToolStripMenuItem("🔍 Diagnostic Mode")
        {
            Checked = false,
            CheckOnClick = true
        };
        _diagnosticItem.Click += OnToggleDiagnostic;

        // Auto-enable startup on first run
        if (!IsStartupEnabled())
        {
            SetStartup(true);
            _startupItem.Checked = true;
        }

        var contextMenu = new ContextMenuStrip();
        string roleLabel = _isRemoteSession ? "[REMOTE]" : (_isShadowSession ? "[SHADOW]" : "[LOCAL]");
        contextMenu.Items.Add(new ToolStripMenuItem($"RDPClipGuard v2.0 {roleLabel}") { Enabled = false, Font = new Font(contextMenu.Font, FontStyle.Bold) });
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_statusItem);
        contextMenu.Items.Add(_copyCountItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Reset rdpclip Now", null, OnResetNow);
        contextMenu.Items.Add(_startupItem);
        contextMenu.Items.Add(_diagnosticItem);
        contextMenu.Items.Add("Open Log File", null, OnOpenLogFile);
        contextMenu.Items.Add("Open Log Folder", null, OnOpenLogFolder);
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

        try
        {
            var parent = _statusItem.GetCurrentParent();
            if (parent != null && parent.InvokeRequired)
                parent.BeginInvoke(() => UpdateUI(message));
            else
                UpdateUI(message);
        }
        catch
        {
            // UI might be disposing
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
            // Kill existing rdpclip processes
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("rdpclip"))
            {
                try { proc.Kill(); } catch { }
            }

            // Wait for process to fully exit
            Thread.Sleep(500);

            // Start new rdpclip.exe
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "rdpclip.exe",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            System.Diagnostics.Process.Start(startInfo);

            // Wait for rdpclip to be responsive (not just started)
            // This prevents clipboard deadlock during startup
            WaitForRdpClipReady(5000); // 5 second timeout
        }
        catch { }
    }

    /// <summary>
    /// Waits for rdpclip.exe to be fully operational (responsive AND clipboard accessible).
    /// This prevents clipboard deadlock by ensuring rdpclip has fully initialized.
    /// </summary>
    private static void WaitForRdpClipReady(int timeoutMs)
    {
        var startTime = DateTime.UtcNow;
        while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("rdpclip");
            bool ready = false;
            try
            {
                if (processes.Length > 0 && processes[0].Responding && CanAccessClipboard())
                    ready = true;
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
            if (ready) return;
            Thread.Sleep(100);
        }
        // Timeout reached - proceed anyway
    }

    /// <summary>
    /// Tests if clipboard is accessible without blocking.
    /// Returns true only if clipboard can be opened and closed successfully.
    /// </summary>
    private static bool CanAccessClipboard()
    {
        try
        {
            // Try to open clipboard - if rdpclip is still initializing, this will fail
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                return false;

            // Successfully opened, close it
            NativeMethods.CloseClipboard();
            return true;
        }
        catch
        {
            return false;
        }
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

    private void OnToggleDiagnostic(object? sender, EventArgs e)
    {
        if (_diagnosticItem.Checked)
        {
            // Enable diagnostics
            string role = _isRemoteSession ? "REMOTE" : (_isShadowSession ? "SHADOW" : "LOCAL");
            try
            {
                _monitor.EnableDiagnostics(role);

                var logFileName = _monitor.LogFilePath != null
                    ? Path.GetFileName(_monitor.LogFilePath)
                    : null;
                var tipMsg = logFileName != null
                    ? $"Diagnostic mode enabled\nLogging to: {logFileName}"
                    : "Diagnostic mode enabled";
                _trayIcon.ShowBalloonTip(3000, "RDPClipGuard", tipMsg, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                _trayIcon.ShowBalloonTip(3000, "RDPClipGuard",
                    $"Error enabling diagnostic mode: {ex.Message}", ToolTipIcon.Error);
            }
        }
        else
        {
            // Disable diagnostics
            _monitor.DisableDiagnostics();
            _trayIcon.ShowBalloonTip(3000, "RDPClipGuard", "Diagnostic mode disabled", ToolTipIcon.Info);
        }
    }

    private void OnOpenLogFile(object? sender, EventArgs e)
    {
        if (!_monitor.DiagnosticsEnabled)
        {
            MessageBox.Show("Diagnostic mode is not enabled. Enable it first from the menu.",
                "No Log File", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var logPath = _monitor.LogFilePath;
        if (logPath == null || !File.Exists(logPath))
        {
            MessageBox.Show("No diagnostic log file found.",
                "Log Not Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = logPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error opening log file: {ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnOpenLogFolder(object? sender, EventArgs e)
    {
        var logDir = _monitor.LogDirectory ?? AppContext.BaseDirectory;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = logDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error opening log folder: {ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _monitor.Stop();
        _trayIcon.Visible = false;
        Application.Exit(); // Dispose() will be called by ApplicationContext teardown
    }

    private static Icon CreateDefaultIcon()
    {
        // Try to load the embedded icon resource first
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));

            if (resourceName != null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    return new Icon(stream, 32, 32);
                }
            }
        }
        catch
        {
            // Fall through to programmatic icon
        }

        // Fallback: create icon programmatically
        using var bmp = new Bitmap(32, 32);
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

        // GetHicon creates an HICON that Icon.FromHandle does not own (won't DestroyIcon on GC).
        // Clone to get an owned copy, then destroy the raw HICON immediately.
        IntPtr hIcon = bmp.GetHicon();
        var ownedIcon = (Icon)Icon.FromHandle(hIcon).Clone();
        NativeMethods.DestroyIcon(hIcon);
        return ownedIcon;
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

}
