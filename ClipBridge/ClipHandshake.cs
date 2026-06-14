using System.IO;
using System.Text;

namespace RDPClipGuard.ClipBridge;

/// <summary>
/// ClipBridge handshake. The server sends the PBKDF2 salt in cleartext (it is not secret); both
/// sides derive the key, then the client proves knowledge of the password by sending a message
/// the server can decrypt. A wrong password makes the server's decrypt fail and it drops the
/// connection.
/// </summary>
internal static class ClipHandshake
{
    public const string Banner = "CLIPBRIDGE/1";
    private const string SaltPrefix = Banner + " SALT=";
    private const int MaxLineBytes = 256;

    /// <summary>
    /// Server side: send the salt, then read and verify the client's encrypted proof.
    /// Returns the derived session key, or throws on failure (wrong password / malformed).
    /// </summary>
    public static async Task<byte[]> ServerAsync(Stream stream, string password, int replayToleranceSeconds, CancellationToken ct)
    {
        byte[] salt = ClipCrypto.GenerateSalt();
        byte[] key = ClipCrypto.DeriveKey(password, salt);

        byte[] line = Encoding.ASCII.GetBytes($"{SaltPrefix}{Convert.ToBase64String(salt)}\n");
        await stream.WriteAsync(line, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        // Decrypting this proves the client used the same password. Wrong password → throws.
        ClipMessage proof = await ClipProtocol.ReadMessageAsync(stream, key, ct).ConfigureAwait(false);
        if (proof.Type != MessageType.Handshake)
            throw new InvalidDataException($"Expected Handshake, received {proof.Type}.");
        if (Math.Abs(ClipMessage.NowUnixMs() - proof.Timestamp) > replayToleranceSeconds * 1000L)
            throw new InvalidDataException("Handshake timestamp outside the allowed skew window.");

        await ClipProtocol.WriteMessageAsync(stream, key, ClipMessage.Control(MessageType.HandshakeOk, 0), ct)
            .ConfigureAwait(false);
        return key;
    }

    /// <summary>
    /// Client side: read the salt, derive the key, send the encrypted proof, await the ack.
    /// Returns the derived session key, or throws on failure.
    /// </summary>
    public static async Task<byte[]> ClientAsync(Stream stream, string password, CancellationToken ct)
    {
        string banner = await ReadLineAsync(stream, ct).ConfigureAwait(false);
        if (!banner.StartsWith(SaltPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected handshake banner from server.");

        byte[] salt;
        try { salt = Convert.FromBase64String(banner[SaltPrefix.Length..].Trim()); }
        catch (FormatException) { throw new InvalidDataException("Malformed salt in handshake banner."); }

        byte[] key = ClipCrypto.DeriveKey(password, salt);
        await ClipProtocol.WriteMessageAsync(stream, key, ClipMessage.Control(MessageType.Handshake, 0), ct)
            .ConfigureAwait(false);

        ClipMessage ack;
        try { ack = await ClipProtocol.ReadMessageAsync(stream, key, ct).ConfigureAwait(false); }
        catch (EndOfStreamException)
        {
            throw new InvalidOperationException(
                "Handshake failed — the server closed the connection (wrong password?).");
        }

        if (ack.Type != MessageType.HandshakeOk)
            throw new InvalidDataException($"Expected HandshakeOk, received {ack.Type}.");
        return key;
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxLineBytes];
        var single = new byte[1];
        int count = 0;
        while (count < MaxLineBytes)
        {
            int n = await stream.ReadAsync(single.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("Connection closed during the handshake banner.");
            if (single[0] == (byte)'\n')
                return Encoding.ASCII.GetString(buffer, 0, count).TrimEnd('\r');
            buffer[count++] = single[0];
        }
        throw new InvalidDataException("Handshake banner exceeded the maximum line length.");
    }
}
