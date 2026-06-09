using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace RDPClipGuard;

/// <summary>
/// Event-based clipboard monitoring using WM_CLIPBOARDUPDATE message.
/// More efficient than polling - receives notification immediately on any clipboard change.
/// Requires Windows Vista and later.
/// </summary>
public sealed class ClipboardListener : IDisposable
{
    private readonly HiddenClipboardWindow _window;
    private uint _lastSequenceNumber;
    private bool _disposed;
    private System.Threading.Timer? _processEventTimer;
    private volatile bool _clipboardChanged;
    private DiagnosticLogger? _diagnosticLogger;
    private readonly object _clipboardAccessLock; // Shared lock from ClipboardMonitor to serialize clipboard access

    public event EventHandler<ClipboardChangeEventArgs>? ClipboardChanged;

    public bool IsListening => !_disposed && _window.IsRegistered && _window.IsHandleValid;

    public bool IsHandleValid => _window.IsHandleValid;

    /// <summary>
    /// Returns whether registration succeeded on last Start() call.
    /// </summary>
    public bool IsRegistrationSuccessful { get; private set; }

    public ClipboardListener(object clipboardAccessLock, DiagnosticLogger? diagnosticLogger = null)
    {
        _clipboardAccessLock = clipboardAccessLock;
        _diagnosticLogger = diagnosticLogger;
        _window = new HiddenClipboardWindow(diagnosticLogger);
        _window.ClipboardUpdateReceived += OnClipboardUpdateReceived;
        _lastSequenceNumber = NativeMethods.GetClipboardSequenceNumber();
    }

    /// <summary>
    /// Starts listening for clipboard changes. Call this after creating the listener.
    /// </summary>
    public void Start()
    {
        IsRegistrationSuccessful = _window.RegisterForNotifications();
        if (!IsRegistrationSuccessful)
        {
            _diagnosticLogger?.LogWarning($"[LISTENER] Registration failed, but continuing with timer-based fallback");
        }
        _processEventTimer ??= new System.Threading.Timer(ProcessClipboardEvent, null, 0, 100);
    }

    /// <summary>
    /// Stops listening for clipboard changes.
    /// </summary>
    public void Stop()
    {
        _processEventTimer?.Dispose();
        _processEventTimer = null;
        _clipboardChanged = false;
        _window.UnregisterFromNotifications();
    }

    private void OnClipboardUpdateReceived(object? sender, EventArgs e)
    {
        _clipboardChanged = true;
    }

    private void ProcessClipboardEvent(object? state)
    {
        if (!_clipboardChanged || _disposed)
            return;

        _clipboardChanged = false;

        if (!Monitor.TryEnter(_clipboardAccessLock, 300))
        {
            _diagnosticLogger?.LogWarning("[LISTENER] Clipboard access lock timeout - skipping event");
            return;
        }

        try
        {
            uint currentSequence = NativeMethods.GetClipboardSequenceNumber();

            var (isFileDrop, text, formats) = ReadClipboard();

            // Skip file drops and locked clipboard
            if (isFileDrop || text == null)
                return;

            var args = new ClipboardChangeEventArgs
            {
                Timestamp = DateTime.Now,
                SequenceNumberChanged = _lastSequenceNumber != currentSequence,
                PreviousSequenceNumber = _lastSequenceNumber,
                CurrentSequenceNumber = currentSequence,
                ClipboardText = text,
                TextHash = text.Length > 0 ? ComputeHash(text) : null,
                Formats = formats
            };

            _lastSequenceNumber = currentSequence;
            ClipboardChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _diagnosticLogger?.LogError($"ProcessClipboardEvent failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_clipboardAccessLock);
        }
    }

    /// <summary>
    /// Opens the clipboard once and reads formats + text in a single session.
    /// Returns (isFileDrop=true, null, formats) for file drops.
    /// Returns (false, null, empty) when clipboard is locked or timeout.
    /// Includes timeout protection (500ms max).
    /// </summary>
    private static (bool isFileDrop, string? text, List<ClipboardFormatInfo> formats) ReadClipboard()
    {
        var startTime = DateTime.UtcNow;
        const int TimeoutMs = 500;

        // Try to open clipboard with timeout
        bool opened = false;
        while (!opened && (DateTime.UtcNow - startTime).TotalMilliseconds < TimeoutMs)
        {
            opened = NativeMethods.OpenClipboard(IntPtr.Zero);
            if (!opened)
                Thread.Sleep(50);
        }

        if (!opened)
            return (false, null, new List<ClipboardFormatInfo>());

        try
        {
            bool isFileDrop = false;
            var formats = new List<ClipboardFormatInfo>();

            uint fmt = 0;
            while ((fmt = NativeMethods.EnumClipboardFormats(fmt)) != 0)
            {
                if (fmt == NativeMethods.CF_HDROP)
                    isFileDrop = true;

                string name = NativeMethods.GetFormatName(fmt);
                if (fmt >= 0xC000)
                {
                    var sb = new StringBuilder(256);
                    if (NativeMethods.GetClipboardFormatName(fmt, sb, 256) > 0)
                        name = sb.ToString();
                }
                formats.Add(new ClipboardFormatInfo { FormatId = fmt, FormatName = name });
            }

            if (isFileDrop)
                return (true, null, formats);

            // Read text via P/Invoke — no STA thread needed
            string? text = null;
            IntPtr hData = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
            if (hData != IntPtr.Zero)
            {
                IntPtr ptr = NativeMethods.GlobalLock(hData);
                if (ptr != IntPtr.Zero)
                {
                    try { text = Marshal.PtrToStringUni(ptr) ?? ""; }
                    finally { NativeMethods.GlobalUnlock(hData); }
                }
            }
            else
            {
                text = "";
            }

            return (false, text, formats);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>
    /// Computes a SHA256 hash of the clipboard text for comparison.
    /// </summary>
    private static string ComputeHash(string text)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        return BitConverter.ToString(hash).Replace("-", "").Substring(0, 8);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _processEventTimer?.Dispose();
            _processEventTimer = null;
            _window?.Dispose();
        }
    }

    /// <summary>
    /// Hidden window that receives WM_CLIPBOARDUPDATE messages.
    /// Validates window handle state and logs errors.
    /// </summary>
    private sealed class HiddenClipboardWindow : NativeWindow, IDisposable
    {
        private bool _isRegistered;
        private bool _disposed;
        private DiagnosticLogger? _diagnosticLogger;

        public bool IsRegistered => _isRegistered && Handle != IntPtr.Zero;
        public bool IsHandleValid => Handle != IntPtr.Zero && NativeMethods.IsWindow(Handle);

        public event EventHandler? ClipboardUpdateReceived;

        public HiddenClipboardWindow(DiagnosticLogger? diagnosticLogger = null)
        {
            _diagnosticLogger = diagnosticLogger;
            // Create as a message-only window (invisible, no events)
            var createParams = new CreateParams
            {
                Parent = NativeMethods.HWND_MESSAGE,
                Style = 0,
                ExStyle = 0,
                ClassStyle = 0,
                Caption = "RDPClipGuard Hidden Window"
            };

            try
            {
                CreateHandle(createParams);
                _diagnosticLogger?.LogDiagnostic($"[LISTENER] Hidden window created: handle={Handle.ToInt64():X}");
            }
            catch (Exception ex)
            {
                _diagnosticLogger?.LogError($"[LISTENER] Failed to create hidden window: {ex.Message}");
            }
        }

        public bool RegisterForNotifications()
        {
            if (_isRegistered || Handle == IntPtr.Zero)
                return _isRegistered;

            try
            {
                if (!IsHandleValid)
                {
                    _diagnosticLogger?.LogWarning($"[LISTENER] RegisterForNotifications: handle invalid, skipping registration");
                    return false;
                }

                bool success = NativeMethods.AddClipboardFormatListener(Handle);
                if (success)
                {
                    _isRegistered = true;
                    _diagnosticLogger?.LogDiagnostic($"[LISTENER] RegisterForNotifications: SUCCESS, handle={Handle.ToInt64():X}");
                }
                else
                {
                    _diagnosticLogger?.LogWarning($"[LISTENER] RegisterForNotifications: AddClipboardFormatListener returned false");
                }
                return success;
            }
            catch (Exception ex)
            {
                _diagnosticLogger?.LogError($"[LISTENER] RegisterForNotifications failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public bool UnregisterFromNotifications()
        {
            if (!_isRegistered || Handle == IntPtr.Zero)
                return true;

            try
            {
                if (!IsHandleValid)
                {
                    _diagnosticLogger?.LogWarning($"[LISTENER] UnregisterFromNotifications: handle invalid, marking as unregistered");
                    _isRegistered = false;
                    return true;
                }

                bool success = NativeMethods.RemoveClipboardFormatListener(Handle);
                if (success)
                {
                    _isRegistered = false;
                    _diagnosticLogger?.LogDiagnostic($"[LISTENER] UnregisterFromNotifications: SUCCESS");
                }
                else
                {
                    _diagnosticLogger?.LogWarning($"[LISTENER] UnregisterFromNotifications: RemoveClipboardFormatListener returned false");
                }
                return success;
            }
            catch (Exception ex)
            {
                _diagnosticLogger?.LogError($"[LISTENER] UnregisterFromNotifications failed: {ex.GetType().Name}: {ex.Message}");
                _isRegistered = false;
                return false;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
            {
                ClipboardUpdateReceived?.Invoke(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                UnregisterFromNotifications();
                if (Handle != IntPtr.Zero)
                {
                    try
                    {
                        DestroyHandle();
                        _diagnosticLogger?.LogDiagnostic($"[LISTENER] Hidden window destroyed");
                    }
                    catch (Exception ex)
                    {
                        _diagnosticLogger?.LogError($"[LISTENER] Failed to destroy window handle: {ex.Message}");
                    }
                }
            }
        }
    }
}

/// <summary>
/// Information about a single clipboard format.
/// </summary>
public class ClipboardFormatInfo
{
    public uint FormatId { get; set; }
    public string FormatName { get; set; } = "";

    public override string ToString() => $"{FormatName} (0x{FormatId:X4})";
}

/// <summary>
/// Event arguments for clipboard change notifications.
/// </summary>
public class ClipboardChangeEventArgs : EventArgs
{
    public DateTime Timestamp { get; set; }
    public uint PreviousSequenceNumber { get; set; }
    public uint CurrentSequenceNumber { get; set; }
    public bool SequenceNumberChanged { get; set; }
    public string? ClipboardText { get; set; }
    public string? TextHash { get; set; }
    public List<ClipboardFormatInfo> Formats { get; set; } = new();

    public string FormatList => string.Join(", ", Formats.Select(f => f.FormatName));
}
