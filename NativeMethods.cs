using System.Runtime.InteropServices;
using System.Text;

namespace RDPClipGuard;

/// <summary>
/// Win32 P/Invoke declarations for clipboard monitoring and diagnostics.
/// </summary>
internal static class NativeMethods
{
    // Clipboard messages
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    // Special window parent for message-only windows
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    // Standard clipboard formats
    public const uint CF_TEXT = 1;
    public const uint CF_BITMAP = 2;
    public const uint CF_METAFILEPICT = 3;
    public const uint CF_SYLK = 4;
    public const uint CF_DIF = 5;
    public const uint CF_TIFF = 6;
    public const uint CF_OEMTEXT = 7;
    public const uint CF_DIB = 8;
    public const uint CF_PALETTE = 9;
    public const uint CF_PENDATA = 10;
    public const uint CF_RIFF = 11;
    public const uint CF_WAVE = 12;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_ENHMETAFILE = 14;
    public const uint CF_HDROP = 15;
    public const uint CF_LOCALE = 16;
    public const uint CF_DIBV5 = 17;

    // Custom format ID ranges
    public const uint CF_PRIVATEFIRST = 0x0200;
    public const uint CF_PRIVATELAST = 0x02FF;
    public const uint CF_GDIOBJFIRST = 0x0300;
    public const uint CF_GDIOBJLAST = 0x03FF;

    /// <summary>
    /// Retrieves the clipboard sequence number (increments on every clipboard change).
    /// </summary>
    [DllImport("user32.dll", SetLastError = false, ExactSpelling = true)]
    public static extern uint GetClipboardSequenceNumber();

    /// <summary>
    /// Registers for clipboard format notifications (WM_CLIPBOARDUPDATE).
    /// More efficient than polling GetClipboardSequenceNumber().
    /// Requires Windows Vista and later.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    /// <summary>
    /// Unregisters from clipboard format notifications.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    /// <summary>
    /// Opens the clipboard for access. Must be paired with CloseClipboard().
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    /// <summary>
    /// Closes the clipboard after access.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    /// <summary>
    /// Enumerates clipboard formats. Start with format=0, continue until it returns 0.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint EnumClipboardFormats(uint format);

    /// <summary>
    /// Gets the name of a clipboard format (for custom formats >= 0xC000).
    /// </summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetClipboardFormatName(uint format, StringBuilder lpszFormatName, int cchMaxCount);

    /// <summary>
    /// Sets the parent window (used to create message-only windows with HWND_MESSAGE).
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    /// <summary>
    /// Gets a human-readable name for standard clipboard format IDs.
    /// </summary>
    public static string GetFormatName(uint format)
    {
        return format switch
        {
            CF_TEXT => "CF_TEXT (ANSI Text)",
            CF_BITMAP => "CF_BITMAP (Bitmap)",
            CF_METAFILEPICT => "CF_METAFILEPICT (Metafile)",
            CF_SYLK => "CF_SYLK (Symbolic Link)",
            CF_DIF => "CF_DIF (Data Interchange Format)",
            CF_TIFF => "CF_TIFF (TIFF Image)",
            CF_OEMTEXT => "CF_OEMTEXT (OEM Text)",
            CF_DIB => "CF_DIB (Device-Independent Bitmap)",
            CF_PALETTE => "CF_PALETTE (Color Palette)",
            CF_PENDATA => "CF_PENDATA (Pen Data)",
            CF_RIFF => "CF_RIFF (Audio Data)",
            CF_WAVE => "CF_WAVE (Wave Audio)",
            CF_UNICODETEXT => "CF_UNICODETEXT (Unicode Text)",
            CF_ENHMETAFILE => "CF_ENHMETAFILE (Enhanced Metafile)",
            CF_HDROP => "CF_HDROP (File Drop)",
            CF_LOCALE => "CF_LOCALE (Locale)",
            CF_DIBV5 => "CF_DIBV5 (Device-Independent Bitmap V5)",
            _ when format >= 0xC000 => $"Custom Format 0x{format:X4}",
            _ => $"Unknown Format 0x{format:X4}"
        };
    }
}
