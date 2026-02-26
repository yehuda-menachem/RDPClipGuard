# RDPClipGuard - Diagnostic Mode Implementation Plan

## Status: READY TO IMPLEMENT

## Background
The app resets rdpclip.exe every 7 copies to prevent RDP clipboard failures.
But the clipboard still fails sometimes, especially with Win+V (clipboard history).
We need diagnostics to pinpoint WHERE the failure happens.

## Root Cause Analysis

The clipboard sync chain has 4 failure points:

```
Controller (LOCAL)                    Controlled (REMOTE)
┌──────────────────┐                  ┌──────────────────┐
│ 1. Clipboard     │                  │ 4. Clipboard     │
│    (Win+V/Ctrl+C)│                  │    (paste target) │
│       ↓          │                  │       ↑          │
│ 2. rdpclip.exe A │ ──RDP Channel── │ 3. rdpclip.exe B │
└──────────────────┘                  └──────────────────┘
```

Current app only monitors point 4 (text polling on remote side).
Need to monitor ALL 4 points on BOTH machines.

## Solution: Same EXE, Two Roles

Auto-detect which side we're on:
```csharp
bool isRemoteSession = SystemInformation.TerminalServerSession;
// true  → running on CONTROLLED/remote machine
// false → running on CONTROLLER/local machine
```

Run the SAME EXE on both machines. Each writes its own timestamped log.
Compare logs to find the exact failure point.

---

## Files to Create/Modify

### 1. NEW: `NativeMethods.cs` — Win32 P/Invoke declarations
```csharp
// GetClipboardSequenceNumber() — detects ANY clipboard change (not just text)
// AddClipboardFormatListener() — event-driven instead of polling
// RemoveClipboardFormatListener()
// OpenClipboard() / CloseClipboard()
// EnumClipboardFormats() / GetClipboardFormatName()
// WM_CLIPBOARDUPDATE = 0x031D
```

### 2. NEW: `ClipboardListener.cs` — Event-based clipboard monitoring
- Hidden NativeWindow with `SetParent(handle, HWND_MESSAGE)`
- Receives `WM_CLIPBOARDUPDATE` instantly (no 2-second delay)
- Reads clipboard sequence number on each change
- Enumerates all clipboard formats (text, image, file, HTML, etc.)
- Fires `ClipboardChanged` event with detailed info:
  - Timestamp (millisecond precision)
  - Sequence number
  - List of formats present
  - Text preview (if text available)
  - Content hash (to compare between machines)

### 3. NEW: `DiagnosticLogger.cs` — Log writer
- Writes to `RDPClipGuard_Diagnostics.log` next to the EXE
- Uses `AppContext.BaseDirectory` (works with single-file publish)
- Format: `[HH:mm:ss.fff] [ROLE] message`
- Role = "LOCAL" or "REMOTE" (auto-detected)
- Auto-rotates: new file each session, keeps last 5
- Thread-safe writing

### 4. NEW: `RdpClipHealth.cs` — rdpclip.exe process diagnostics
- PID, start time, uptime
- Memory usage (WorkingSet, PrivateMemory)
- Handle count (HIGH = resource leak indicator, >1000 = warning)
- CPU time (user + kernel)
- Responding status (Process.Responding)
- Logged every 10 seconds when diagnostic mode is on

### 5. MODIFY: `ClipboardMonitor.cs` — Enhance existing monitor
- Add `GetClipboardSequenceNumber()` check alongside text polling
- Log when sequence number changes but text doesn't (= non-text content)
- Log when text changes but was NOT detected by sequence number (= bug)
- Keep existing reset logic unchanged

### 6. MODIFY: `TrayApplicationContext.cs` — Add diagnostic UI
- Add menu item: "🔍 Diagnostic Mode" (toggle, CheckOnClick)
- When enabled:
  - Show role in title: "RDPClipGuard v1.0 [LOCAL]" or "[REMOTE]"
  - Start ClipboardListener (event-based)
  - Start RdpClipHealth monitoring (every 10s)
  - Start writing to log file
  - Show balloon tip: "Diagnostic mode ON - logging to [path]"
- Add menu item: "Open Log File" (opens in notepad)
- Add menu item: "Open Log Folder" (opens in explorer)

---

## Log Output Example

### On Controller (LOCAL):
```
[17:23:01.234] [LOCAL] === Diagnostic Session Started ===
[17:23:01.235] [LOCAL] OS: Windows 11 Pro | RDP Session: No
[17:23:01.236] [LOCAL] rdpclip: PID=1234 | Uptime=5.2min | Handles=89 | Mem=4MB
[17:23:05.100] [LOCAL] CLIPBOARD CHANGED | Seq: 142→143 | Formats: CF_UNICODETEXT,CF_TEXT,CF_LOCALE
[17:23:05.101] [LOCAL] Text: "hello world" | Hash: a591a6d4
[17:23:05.102] [LOCAL] rdpclip: PID=1234 | Responding=True | Handles=91
[17:23:12.500] [LOCAL] CLIPBOARD CHANGED (Win+V) | Seq: 143→144 | Formats: CF_UNICODETEXT,CF_TEXT
[17:23:12.501] [LOCAL] Text: "previous item" | Hash: b6589fc6
[17:23:12.502] [LOCAL] rdpclip: PID=1234 | Responding=True | Handles=91
```

### On Controlled (REMOTE):
```
[17:23:01.500] [REMOTE] === Diagnostic Session Started ===
[17:23:01.501] [REMOTE] OS: Windows 11 Pro | RDP Session: Yes
[17:23:01.502] [REMOTE] rdpclip: PID=5678 | Uptime=5.2min | Handles=45 | Mem=3MB
[17:23:05.300] [REMOTE] CLIPBOARD CHANGED | Seq: 88→89 | Formats: CF_UNICODETEXT,CF_TEXT,CF_LOCALE
[17:23:05.301] [REMOTE] Text: "hello world" | Hash: a591a6d4 ← MATCH with LOCAL
[17:23:12.800] [REMOTE] *** NO CLIPBOARD CHANGE DETECTED *** (waited 5s after LOCAL changed)
[17:23:12.801] [REMOTE] rdpclip: PID=5678 | Responding=True | Handles=45
[17:23:12.802] [REMOTE] >>> DIAGNOSIS: rdpclip on REMOTE did not receive clipboard update from LOCAL
```

## Comparing Logs (User Action)
User reproduces the issue, then compares both log files:
- Same hash = sync worked
- Different hash = sync failed
- No change on remote = rdpclip channel broken
- Sequence changed but no text = non-text content (Win+V image/file)
- Handles increasing = resource leak → need earlier reset

---

## Implementation Order
1. `NativeMethods.cs` — Win32 declarations
2. `DiagnosticLogger.cs` — Log writer
3. `ClipboardListener.cs` — Event-based monitoring
4. `RdpClipHealth.cs` — Process diagnostics
5. Modify `ClipboardMonitor.cs` — Add sequence number tracking
6. Modify `TrayApplicationContext.cs` — Add diagnostic menu items
7. Test build, commit, update release

## Key Design Decisions
- **Portable**: Log file next to EXE (AppContext.BaseDirectory)
- **No dependencies**: Pure Win32 P/Invoke, no NuGet packages
- **Opt-in**: Diagnostic mode is OFF by default, toggle via menu
- **Same EXE**: Auto-detects role (local/remote) via SystemInformation.TerminalServerSession
- **Non-invasive**: Existing clipboard reset logic unchanged
