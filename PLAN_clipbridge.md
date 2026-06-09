# RDPClipGuard — ClipBridge Implementation Plan (revised)

## Status: READY TO IMPLEMENT

## Purpose
ClipBridge adds an **independent, encrypted TCP channel** that synchronizes clipboard
**text** between the LOCAL machine and a REMOTE (RDP) session — separate from the built-in
`rdpclip` redirection.

It is **mode-agnostic** by design:
- **Replacement mode** — RDP clipboard redirection is disabled/blocked by policy; ClipBridge
  is the only channel.
- **Coexistence mode** — RDP redirection is also active; ClipBridge runs alongside it.

The anti-echo logic is **content-hash based** (not a timing flag), so it is correct in *both*
modes without configuration. Copying the same text via two channels collapses to a single
no-op because the content hash matches.

---

## Corrections folded in from the design review

| # | Problem in the original draft | Fix in this plan |
|---|-------------------------------|------------------|
| 1 | `ClipboardListener` only runs in diagnostic mode — `OnClipboardListenerChanged` bails when `_diagnosticLogger == null` (`ClipboardMonitor.cs:211-213`); listener is created only inside `EnableDiagnostics()` (`ClipboardMonitor.cs:158`). The plan's `Attach(ClipboardMonitor)` would never receive events. | **Decouple the listener from diagnostics.** Start it in `ClipboardMonitor.Start()` and expose a public `ClipboardTextChanged` event that always fires. (Phase 2) |
| 2 | Handshake salt ordering was circular: client must encrypt `HELLO` with `PBKDF2(password, salt)`, but salt is only sent by the server *afterward*. | **Server sends salt in cleartext first** (salt is not secret). Both derive the key, then the client proves knowledge of the password by sending an AES-GCM-encrypted handshake the server can decrypt. (Phase 1) |
| 3 | Anti-loop used a transient `volatile bool _isSettingClipboard`, which races the 100ms listener timer and the 2s poller; bridge writes also inflate `_copyCount` and trigger `rdpclip` resets every 7 copies (`ClipboardMonitor.cs:25`, `:261-291`). | **Content-hash self-write suppression** via `NotifySelfWrite(text)`. Suppressed events neither echo nor count toward the reset budget. (Phase 2/4) |
| 4 | Bridge wrote the clipboard from a background thread, bypassing `_clipboardAccessLock` (`ClipboardMonitor.cs:35`). | All bridge writes go through `ClipboardMonitor.SetClipboardText()`, which takes the existing shared lock. (Phase 2) |
| 5 | 4-byte length prefix with no cap → giant allocation from a malicious/garbage peer. | Enforce `MaxFrameSize` (8 MB) before allocating. (Phase 1) |
| 6 | Anti-replay used a hard ±30s window — RDP endpoints often have clock skew, and replay was still possible inside the window. | Configurable tolerance (default 60s) **plus** a per-session seen-`seq` dedup window. (Phase 1) |
| 7 | `SetClipboardData` memory ownership unspecified. | Document: allocate with `GMEM_MOVEABLE`; after a successful `SetClipboardData` the OS **owns** the handle (do **not** `GlobalFree`); free only on failure. (Phase 2) |
| 8 | Server "accepts one connection" — no path to recover after the client drops. | Server runs an **accept loop**; a new connection replaces the previous session. (Phase 3) |
| 9 | Password planned to be stored in plaintext JSON. | Store password **DPAPI-encrypted** (`ProtectedData`, CurrentUser scope) in `bridge.json`. (Phase 5) |

---

## Architecture changes to existing code (do this first)

### A. Decouple `ClipboardListener` from diagnostics — `ClipboardMonitor.cs`
- Create and `Start()` the listener inside `ClipboardMonitor.Start()` (always on), not inside
  `EnableDiagnostics()`. Diagnostic *logging* stays gated on `_diagnosticsEnabled`; the listener
  itself does not.
- In `OnClipboardListenerChanged`, remove the early `return` when `_diagnosticLogger == null`.
  Keep all logging calls null-safe (`_diagnosticLogger?.…`).

### B. Public clipboard-change signal — `ClipboardMonitor.cs`
```csharp
public sealed class ClipboardTextChange
{
    public string Text { get; init; } = "";
    public string Hash { get; init; } = "";   // reuse the existing 8-char SHA256 prefix
}

// Fires on a genuine text change (not on a self-write we just applied).
public event Action<ClipboardTextChange>? ClipboardTextChanged;
```
Raise it from the same place that currently detects a changed copy (the listener path and/or the
poller path in `CheckClipboard`), after the self-write check below.

### C. Self-write suppression + lock-coordinated write — `ClipboardMonitor.cs`
```csharp
// Bridge calls this just before/while applying remote text.
public void NotifySelfWrite(string text);   // records hash + timestamp in a short ring buffer

// Bridge applies remote text through here so it serializes on _clipboardAccessLock.
public bool SetClipboardText(string text);   // OpenClipboard → EmptyClipboard → SetClipboardData
```
Suppression rule, applied in BOTH the listener and poller paths:
> If the current clipboard text hash matches a `NotifySelfWrite` hash recorded within the last
> few seconds, **skip it**: do not increment `_copyCount`, do not fire `ClipboardTextChanged`,
> do not trigger a reset. Then expire that hash.

This is what makes coexistence mode safe: an `rdpclip`-delivered duplicate of the same text we
just applied hashes identically and is dropped.

---

## New files — `ClipBridge/`

### `ClipCrypto.cs`
```csharp
public static class ClipCrypto
{
    // PBKDF2-SHA256, 200_000 iterations → 32-byte key.
    public static byte[] DeriveKey(string password, byte[] salt);

    // AES-256-GCM → [12-byte nonce | ciphertext | 16-byte tag]. Nonce = RandomNumberGenerator.
    public static byte[] Encrypt(byte[] key, byte[] plaintext);

    // Splits nonce/tag, verifies tag, throws CryptographicException on tamper/wrong key.
    public static byte[] Decrypt(byte[] key, byte[] blob);
}
```

### `ClipProtocol.cs`
```csharp
public enum MessageType { Handshake, HandshakeOk, ClipboardUpdate, Ping, Pong }

public sealed class ClipMessage
{
    public MessageType Type { get; set; }
    public string? Text     { get; set; }
    public long Timestamp   { get; set; }  // unix ms
    public long Seq         { get; set; }  // monotonic per session, for dedup
}
```
Wire framing: `[4-byte big-endian length][encrypted payload]`.
- `const int MaxFrameSize = 8 * 1024 * 1024;` — reject (and drop the connection) before allocating
  if `length > MaxFrameSize` or `length <= 0`.
- Payload = AES-GCM-encrypted UTF-8 JSON of `ClipMessage`.
- Anti-replay on receive: reject if `|now - Timestamp| > ToleranceMs` (default 60_000) **or** if
  `Seq` was already seen in the session's sliding window.

### Handshake (corrected order)
```
1. Server → Client (cleartext):  "CLIPBRIDGE/1 SALT=<base64(16 random bytes)>\n"
2. Both derive:                  key = PBKDF2(password, salt)
3. Client → Server (encrypted):  ClipMessage { Type=Handshake,   Timestamp=now, Seq=0 }
4. Server decrypts+verifies tag+timestamp.
     - success → Server → Client (encrypted): ClipMessage { Type=HandshakeOk, Timestamp=now }
     - failure → Server closes the socket (wrong password / tampered).
5. Channel established → both sides exchange ClipboardUpdate / Ping / Pong.
```
The salt travels in cleartext (it is not secret). Proof-of-password is the server's ability to
decrypt step 3.

### `ClipBridgeServer.cs`
```csharp
public sealed class ClipBridgeServer : IDisposable
{
    public Task StartAsync(int port, byte[] keyOrPassword, CancellationToken ct);  // accept LOOP
    public event EventHandler<string>? RemoteClipboardReceived;
    public event EventHandler<bool>?   ClientConnectionChanged;  // true=connected
    public Task SendClipboardAsync(string text);
}
```
- `Accept` loop: a new accepted connection replaces the previous active session (close the old
  one). Survives client reconnects.
- Per connection: server-side handshake → framed read/write loop. Any decrypt/parse/oversize
  error → close that connection and keep accepting.

### `ClipBridgeClient.cs`
```csharp
public sealed class ClipBridgeClient : IDisposable
{
    public Task ConnectAsync(string host, int port, string password, CancellationToken ct);
    public event EventHandler<string>? RemoteClipboardReceived;
    public event EventHandler<bool>?   ConnectionChanged;
    public Task SendClipboardAsync(string text);
}
```

### `BridgeManager.cs`
```csharp
public sealed class BridgeManager : IDisposable
{
    public void Attach(ClipboardMonitor monitor);           // subscribes to ClipboardTextChanged
    public Task StartAsync(BridgeSettings settings);        // server XOR client by Mode
    public Task StopAsync();
    public event EventHandler<BridgeStatus>? StatusChanged;  // for the tray UI
}
```
Anti-echo (content-hash, both directions):
```
On ClipboardTextChanged(e):                      // local change to broadcast
    if e.Hash == _lastAppliedHash || e.Hash == _lastSentHash: return;  // echo
    _lastSentHash = e.Hash;
    await peer.SendClipboardAsync(e.Text);

On RemoteClipboardReceived(text):                // remote change to apply
    var hash = Hash(text);
    if hash == _lastSentHash: return;            // our own value bounced back
    _lastAppliedHash = hash;
    _monitor.NotifySelfWrite(text);              // suppress the resulting change event
    _monitor.SetClipboardText(text);             // write under _clipboardAccessLock
```
Reconnect (client): exponential backoff 2s → 4s → 8s → … capped at 60s, reset on success.
Server side recovers via its accept loop. `rdpclip` resets: because remote-applied writes call
`NotifySelfWrite`, they don't inflate `_copyCount`, so the bridge does not provoke extra resets.

### `BridgeSettings.cs`
```csharp
public enum BridgeMode { Server, Client }

public sealed class BridgeSettings
{
    public BridgeMode Mode { get; set; } = BridgeMode.Server;
    public string Host     { get; set; } = "";      // client only
    public int Port        { get; set; } = 9512;
    public string Password { get; set; } = "";       // held in memory only
    public bool AutoConnect { get; set; }
    public int ReplayToleranceSeconds { get; set; } = 60;

    public static BridgeSettings Load();   // %AppData%\RDPClipGuard\bridge.json
    public void Save();
}
```
Password at rest: serialize a **DPAPI-protected** blob
(`ProtectedData.Protect(utf8(password), null, DataProtectionScope.CurrentUser)`, base64) — never
the plaintext. `Load()` unprotects it back into `Password`.

### `BridgeSettingsForm.cs`
```
┌─────────────────────────────────────────┐
│  🔗 ClipBridge Settings                  │
├─────────────────────────────────────────┤
│  Mode:  ● LOCAL (Server - wait)          │
│         ○ REMOTE (Client - connect)      │
│  Server IP:  [192.168.1.100    ]         │
│  Port:       [9512             ]         │
│  Password:   [●●●●●●●●●●●●●●●●]  👁      │
│              [●●●●●●●●●●●●●●●●]  (confirm)│
│  ☑ Connect automatically on startup      │
│  Status: 🔴 Not connected                │
│  [Connect]  [Disconnect]  [Test]  [Cancel]│
└─────────────────────────────────────────┘
```

---

## `NativeMethods.cs` additions
```csharp
public const uint GMEM_MOVEABLE = 0x0002;

[DllImport("kernel32.dll", SetLastError = true)]
public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

[DllImport("user32.dll", SetLastError = true)]
public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

[DllImport("user32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
public static extern bool EmptyClipboard();
```
Write sequence (inside `SetClipboardText`, under `_clipboardAccessLock`):
`OpenClipboard(hWnd)` → `EmptyClipboard()` → allocate `GMEM_MOVEABLE` for the null-terminated
UTF-16 string → `GlobalLock`/copy/`GlobalUnlock` → `SetClipboardData(CF_UNICODETEXT, hMem)`.
**Ownership:** on success the system owns `hMem` — do **not** free it. On failure, `GlobalFree(hMem)`.
Always `CloseClipboard()` in `finally`.

---

## `TrayApplicationContext.cs` integration
- Add a `🔗 ClipBridge` submenu: a disabled status line + `Bridge Settings…`.
- Construct a `BridgeManager`, `Attach(_monitor)`, and start it if `AutoConnect`.
- Subscribe to `BridgeManager.StatusChanged`; update the status line via the existing
  `BeginInvoke` marshalling pattern used in `OnStatusChanged` (`TrayApplicationContext.cs:102-118`).
- Dispose the `BridgeManager` in `Dispose`.

---

## Test matrix
| Test | Scenario |
|------|----------|
| Crypto round-trip | `Encrypt` → `Decrypt` returns the original bytes |
| Wrong key | `Decrypt` with a wrong key throws |
| Handshake | Server sends salt, client proves password, `HandshakeOk` received |
| Wrong password | Server fails to decrypt handshake → closes socket; client shows clear error |
| REMOTE → LOCAL | Copy on remote, paste on local |
| LOCAL → REMOTE | Reverse |
| Anti-echo (replacement) | No infinite echo with RDP redirection off |
| Anti-echo (coexistence) | No double-apply / echo with RDP redirection on |
| No spurious resets | Bridge-applied paste does not increment `_copyCount` / trigger `rdpclip` reset |
| Oversize frame | `length > MaxFrameSize` rejected, connection dropped, no large allocation |
| Clock skew | Within tolerance accepted; beyond tolerance rejected |
| Replay | Re-sent `Seq` within the window is dropped |
| Reconnect (client) | Server restarts → client reconnects with backoff |
| Reconnect (server) | Client drops → accept loop takes the next connection |
| Crash recovery | One side crashes → other side reconnects |
| Large text (100 KB) | Transfers without timeout |
| Unicode / Hebrew | Correct UTF-8 ↔ UTF-16 round-trip |
| Password at rest | `bridge.json` contains a DPAPI blob, not plaintext |

---

## Implementation order
1. **Phase 1 — Crypto + Protocol:** `ClipCrypto`, `ClipProtocol`, framing, handshake. Console
   round-trip checks.
2. **Phase 2 — Existing-code refactor:** decouple listener; add `ClipboardTextChanged`,
   `NotifySelfWrite`, `SetClipboardText`; `NativeMethods` additions. *Everything below depends on
   this, so it comes before the transport.*
3. **Phase 3 — Transport:** `ClipBridgeServer` (accept loop) + `ClipBridgeClient`.
4. **Phase 4 — `BridgeManager`:** anti-echo, reconnect/backoff, status.
5. **Phase 5 — Settings + UI:** `BridgeSettings` (DPAPI), `BridgeSettingsForm`.
6. **Phase 6 — Tray integration.**
7. **Phase 7 — Run the test matrix, build, commit, bump version.**

## Key design decisions
- **No NuGet dependencies** — `System.Security.Cryptography.AesGcm`, `Rfc2898DeriveBytes`, and
  `ProtectedData` are all in the BCL.
- **Mode-agnostic anti-echo** — content-hash dedup, correct whether ClipBridge replaces or
  coexists with `rdpclip`.
- **Text only (v1)** — matches the existing listener, which already skips file drops and non-text.
- **Single shared clipboard lock** — bridge writes serialize with the poller and listener.
- **Opt-in** — disabled until configured; `AutoConnect` is off by default.
