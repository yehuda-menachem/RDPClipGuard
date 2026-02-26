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

    public event EventHandler<ClipboardChangeEventArgs>? ClipboardChanged;

    public ClipboardListener()
    {
        _window = new HiddenClipboardWindow();
        _window.ClipboardUpdateReceived += OnClipboardUpdateReceived;
        _lastSequenceNumber = NativeMethods.GetClipboardSequenceNumber();
    }

    /// <summary>
    /// Starts listening for clipboard changes. Call this after creating the listener.
    /// </summary>
    public void Start()
    {
        _window.RegisterForNotifications();
    }

    /// <summary>
    /// Stops listening for clipboard changes.
    /// </summary>
    public void Stop()
    {
        _window.UnregisterFromNotifications();
    }

    private void OnClipboardUpdateReceived(object? sender, EventArgs e)
    {
        try
        {
            uint currentSequence = NativeMethods.GetClipboardSequenceNumber();

            // Get clipboard content and formats
            var text = GetClipboardText();
            var formats = GetClipboardFormats();

            var args = new ClipboardChangeEventArgs
            {
                Timestamp = DateTime.Now,
                SequenceNumberChanged = _lastSequenceNumber != currentSequence,
                PreviousSequenceNumber = _lastSequenceNumber,
                CurrentSequenceNumber = currentSequence,
                ClipboardText = text,
                TextHash = text != null ? ComputeHash(text) : null,
                Formats = formats
            };

            _lastSequenceNumber = currentSequence;
            ClipboardChanged?.Invoke(this, args);
        }
        catch
        {
            // Silently handle errors in clipboard access
        }
    }

    /// <summary>
    /// Gets text from the clipboard safely in an STA thread.
    /// </summary>
    private static string? GetClipboardText()
    {
        string? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    result = Clipboard.GetText();
                }
                else
                {
                    result = "";
                }
            }
            catch
            {
                result = null;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(2000); // 2 second timeout
        return result;
    }

    /// <summary>
    /// Enumerates all clipboard formats currently available.
    /// </summary>
    private static List<ClipboardFormatInfo> GetClipboardFormats()
    {
        var formats = new List<ClipboardFormatInfo>();

        try
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                return formats;

            try
            {
                uint format = 0;
                while ((format = NativeMethods.EnumClipboardFormats(format)) != 0)
                {
                    string formatName = NativeMethods.GetFormatName(format);

                    // For custom formats, try to get the actual name
                    if (format >= 0xC000)
                    {
                        StringBuilder sb = new StringBuilder(256);
                        int result = NativeMethods.GetClipboardFormatName(format, sb, 256);
                        if (result > 0)
                        {
                            formatName = sb.ToString();
                        }
                    }

                    formats.Add(new ClipboardFormatInfo
                    {
                        FormatId = format,
                        FormatName = formatName
                    });
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch
        {
            // If enumeration fails, return empty list
        }

        return formats;
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
            Stop();
            _window?.Dispose();
        }
    }

    /// <summary>
    /// Hidden window that receives WM_CLIPBOARDUPDATE messages.
    /// </summary>
    private sealed class HiddenClipboardWindow : NativeWindow, IDisposable
    {
        private bool _isRegistered;
        private bool _disposed;

        public event EventHandler? ClipboardUpdateReceived;

        public HiddenClipboardWindow()
        {
            // Create as a message-only window (invisible, no events)
            var createParams = new CreateParams
            {
                Parent = NativeMethods.HWND_MESSAGE,
                Style = 0,
                ExStyle = 0,
                ClassStyle = 0,
                Caption = "RDPClipGuard Hidden Window"
            };

            CreateHandle(createParams);
        }

        public void RegisterForNotifications()
        {
            if (!_isRegistered && Handle != IntPtr.Zero)
            {
                try
                {
                    if (NativeMethods.AddClipboardFormatListener(Handle))
                    {
                        _isRegistered = true;
                    }
                }
                catch
                {
                    // Handle creation error
                }
            }
        }

        public void UnregisterFromNotifications()
        {
            if (_isRegistered && Handle != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.RemoveClipboardFormatListener(Handle);
                    _isRegistered = false;
                }
                catch
                {
                    // Handle removal error
                }
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
                    DestroyHandle();
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
