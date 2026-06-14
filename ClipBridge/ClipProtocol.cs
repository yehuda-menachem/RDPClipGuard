using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RDPClipGuard.ClipBridge;

/// <summary>
/// Kinds of messages exchanged over a ClipBridge connection.
/// </summary>
public enum MessageType
{
    /// <summary>Client → Server: encrypted proof-of-password after the salt exchange.</summary>
    Handshake,

    /// <summary>Server → Client: acknowledges a successful handshake.</summary>
    HandshakeOk,

    /// <summary>A clipboard text update to apply on the peer.</summary>
    ClipboardUpdate,

    /// <summary>Keepalive request.</summary>
    Ping,

    /// <summary>Keepalive response.</summary>
    Pong
}

/// <summary>
/// A single ClipBridge message. Serialized to UTF-8 JSON, then encrypted before framing.
/// </summary>
public sealed class ClipMessage
{
    public MessageType Type { get; set; }

    /// <summary>Clipboard text for <see cref="MessageType.ClipboardUpdate"/>; otherwise null.</summary>
    public string? Text { get; set; }

    /// <summary>Unix time in milliseconds when the message was created (anti-replay).</summary>
    public long Timestamp { get; set; }

    /// <summary>Monotonic per-session sequence number (anti-replay / dedup).</summary>
    public long Seq { get; set; }

    public static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static ClipMessage Clipboard(string text, long seq) => new()
    {
        Type = MessageType.ClipboardUpdate,
        Text = text,
        Timestamp = NowUnixMs(),
        Seq = seq
    };

    public static ClipMessage Control(MessageType type, long seq) => new()
    {
        Type = type,
        Timestamp = NowUnixMs(),
        Seq = seq
    };
}

/// <summary>
/// Wire encoding for ClipBridge. Each frame is <c>[4-byte big-endian length][encrypted payload]</c>,
/// where the payload is AES-256-GCM over the UTF-8 JSON of a <see cref="ClipMessage"/>.
/// </summary>
public static class ClipProtocol
{
    /// <summary>
    /// Hard cap on a single frame's encrypted payload. Rejected before allocation to stop a
    /// malicious or corrupt peer from forcing a huge buffer.
    /// </summary>
    public const int MaxFrameSize = 8 * 1024 * 1024;

    private const int LengthPrefixBytes = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Serializes a message to its plaintext UTF-8 JSON form.</summary>
    public static byte[] SerializeMessage(ClipMessage message)
        => JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

    /// <summary>Parses plaintext UTF-8 JSON back into a message.</summary>
    public static ClipMessage DeserializeMessage(byte[] jsonUtf8)
        => JsonSerializer.Deserialize<ClipMessage>(jsonUtf8, JsonOptions)
           ?? throw new InvalidDataException("Message payload deserialized to null.");

    /// <summary>
    /// Builds a complete wire frame for <paramref name="message"/>: encrypts the JSON payload and
    /// prepends the 4-byte big-endian length. Useful for tests and buffered sends.
    /// </summary>
    public static byte[] EncodeFrame(byte[] key, ClipMessage message)
    {
        byte[] payload = ClipCrypto.Encrypt(key, SerializeMessage(message));
        if (payload.Length > MaxFrameSize)
            throw new InvalidDataException($"Encoded frame ({payload.Length} bytes) exceeds MaxFrameSize.");

        byte[] frame = new byte[LengthPrefixBytes + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, LengthPrefixBytes), payload.Length);
        payload.CopyTo(frame.AsSpan(LengthPrefixBytes));
        return frame;
    }

    /// <summary>Decrypts and parses an already-de-framed encrypted payload.</summary>
    public static ClipMessage DecodePayload(byte[] key, byte[] encryptedPayload)
        => DeserializeMessage(ClipCrypto.Decrypt(key, encryptedPayload));

    /// <summary>
    /// Encrypts and writes a single framed message to <paramref name="stream"/>.
    /// </summary>
    public static async Task WriteMessageAsync(Stream stream, byte[] key, ClipMessage message, CancellationToken ct)
    {
        byte[] frame = EncodeFrame(key, message);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one framed message from <paramref name="stream"/>, enforcing <see cref="MaxFrameSize"/>.
    /// Throws <see cref="EndOfStreamException"/> if the peer closes mid-frame, or
    /// <see cref="InvalidDataException"/> if the declared length is out of range.
    /// </summary>
    public static async Task<ClipMessage> ReadMessageAsync(Stream stream, byte[] key, CancellationToken ct)
    {
        byte[] lengthBuffer = new byte[LengthPrefixBytes];
        await ReadExactlyAsync(stream, lengthBuffer, ct).ConfigureAwait(false);

        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
        if (length <= 0 || length > MaxFrameSize)
            throw new InvalidDataException($"Frame length {length} is out of range (1..{MaxFrameSize}).");

        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, ct).ConfigureAwait(false);
        return DecodePayload(key, payload);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("Connection closed before the full frame was received.");
            read += n;
        }
    }
}

/// <summary>
/// Per-session anti-replay guard: rejects messages whose timestamp falls outside the allowed
/// skew window, and de-duplicates sequence numbers within a sliding window.
/// </summary>
public sealed class ReplayGuard
{
    private const int MaxRemembered = 1024;

    private readonly long _toleranceMs;
    private readonly HashSet<long> _seen = new();
    private readonly Queue<long> _order = new();

    public ReplayGuard(int toleranceSeconds)
    {
        if (toleranceSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(toleranceSeconds));
        _toleranceMs = toleranceSeconds * 1000L;
    }

    /// <summary>
    /// Returns true if the message is fresh (within the clock-skew tolerance and not a replayed
    /// sequence number) and records its sequence number. Returns false otherwise.
    /// </summary>
    public bool TryAccept(ClipMessage message, long nowUnixMs)
    {
        if (Math.Abs(nowUnixMs - message.Timestamp) > _toleranceMs)
            return false;

        if (!_seen.Add(message.Seq))
            return false;

        _order.Enqueue(message.Seq);
        if (_order.Count > MaxRemembered)
            _seen.Remove(_order.Dequeue());

        return true;
    }
}
