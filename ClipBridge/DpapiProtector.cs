using System.Runtime.InteropServices;
using System.Text;

namespace RDPClipGuard.ClipBridge;

/// <summary>
/// Encrypts small secrets (the bridge password) at rest using Windows DPAPI for the current user,
/// via direct P/Invoke so no NuGet dependency is required. Output is base64 for JSON storage.
/// </summary>
internal static class DpapiProtector
{
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public uint cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved,
        IntPtr pPromptStruct, uint dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved,
        IntPtr pPromptStruct, uint dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Encrypts a string for the current user; returns base64 ciphertext.</summary>
    public static string Protect(string plaintext)
        => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(plaintext), protect: true));

    /// <summary>Reverses <see cref="Protect"/>. Throws if the blob can't be decrypted.</summary>
    public static string Unprotect(string protectedBase64)
        => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(protectedBase64), protect: false));

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        IntPtr inPtr = Marshal.AllocHGlobal(input.Length == 0 ? 1 : input.Length);
        try
        {
            if (input.Length > 0)
                Marshal.Copy(input, 0, inPtr, input.Length);
            inBlob.cbData = (uint)input.Length;
            inBlob.pbData = inPtr;

            bool ok = protect
                ? CryptProtectData(ref inBlob, "RDPClipGuard.ClipBridge", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);

            if (!ok)
                throw new InvalidOperationException($"DPAPI {(protect ? "protect" : "unprotect")} failed (Win32 error {Marshal.GetLastWin32Error()}).");

            try
            {
                var output = new byte[outBlob.cbData];
                if (outBlob.cbData > 0)
                    Marshal.Copy(outBlob.pbData, output, 0, (int)outBlob.cbData);
                return output;
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero)
                    LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
        }
    }
}
