using System.Security.Cryptography;
using System.Text;

namespace RDPClipGuard.ClipBridge;

/// <summary>
/// Cryptographic primitives for ClipBridge: PBKDF2 key derivation and AES-256-GCM
/// authenticated encryption. No third-party dependencies — all from the BCL.
/// </summary>
/// <remarks>
/// Encrypted blob layout: <c>[12-byte nonce | ciphertext (N bytes) | 16-byte tag]</c>.
/// The salt is exchanged in cleartext during the handshake (it is not secret); proof of
/// the shared password is the peer's ability to decrypt a message under the derived key.
/// </remarks>
public static class ClipCrypto
{
    /// <summary>AES-256 key length in bytes.</summary>
    public const int KeySizeBytes = 32;

    /// <summary>AES-GCM nonce length in bytes (96-bit, the recommended size).</summary>
    public const int NonceSizeBytes = 12;

    /// <summary>AES-GCM authentication tag length in bytes (128-bit, the maximum).</summary>
    public const int TagSizeBytes = 16;

    /// <summary>PBKDF2 salt length in bytes.</summary>
    public const int SaltSizeBytes = 16;

    /// <summary>Minimum bytes a valid encrypted blob can occupy (nonce + tag, zero-length plaintext).</summary>
    public const int MinBlobSize = NonceSizeBytes + TagSizeBytes;

    private const int Pbkdf2Iterations = 200_000;

    /// <summary>
    /// Generates a cryptographically random salt for key derivation.
    /// </summary>
    public static byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(SaltSizeBytes);

    /// <summary>
    /// Derives a 32-byte AES-256 key from a password and salt using PBKDF2-HMAC-SHA256.
    /// </summary>
    public static byte[] DeriveKey(string password, byte[] salt)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password must not be empty.", nameof(password));
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length == 0)
            throw new ArgumentException("Salt must not be empty.", nameof(salt));

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySizeBytes);
    }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> with AES-256-GCM under <paramref name="key"/>.
    /// Returns <c>[nonce | ciphertext | tag]</c> with a fresh random nonce per call.
    /// </summary>
    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(plaintext);

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        byte[] result = new byte[NonceSizeBytes + plaintext.Length + TagSizeBytes];

        // Lay out [nonce | ciphertext | tag] and write directly into the result spans.
        var nonceSpan = result.AsSpan(0, NonceSizeBytes);
        var cipherSpan = result.AsSpan(NonceSizeBytes, plaintext.Length);
        var tagSpan = result.AsSpan(NonceSizeBytes + plaintext.Length, TagSizeBytes);
        nonce.CopyTo(nonceSpan);

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonceSpan, plaintext, cipherSpan, tagSpan);
        return result;
    }

    /// <summary>
    /// Decrypts a <c>[nonce | ciphertext | tag]</c> blob produced by <see cref="Encrypt"/>.
    /// Throws <see cref="CryptographicException"/> if the tag fails to verify (wrong key or
    /// tampered data) or if the blob is malformed.
    /// </summary>
    public static byte[] Decrypt(byte[] key, byte[] blob)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < MinBlobSize)
            throw new CryptographicException("Encrypted blob is too short to contain a nonce and tag.");

        int cipherLen = blob.Length - NonceSizeBytes - TagSizeBytes;
        var nonceSpan = blob.AsSpan(0, NonceSizeBytes);
        var cipherSpan = blob.AsSpan(NonceSizeBytes, cipherLen);
        var tagSpan = blob.AsSpan(NonceSizeBytes + cipherLen, TagSizeBytes);

        byte[] plaintext = new byte[cipherLen];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Decrypt(nonceSpan, cipherSpan, tagSpan, plaintext); // throws on tag mismatch
        return plaintext;
    }

    private static void ValidateKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySizeBytes)
            throw new ArgumentException($"Key must be {KeySizeBytes} bytes (AES-256).", nameof(key));
    }
}
