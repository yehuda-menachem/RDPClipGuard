# Changelog

All notable changes to RDPClipGuard are documented in this file.

## [2.0.1] - 2026-03-04

### Fixed
- **Mutex timeout on Windows restart**: Fixed "RDPClipGuard is already running" error when app crashes or Windows restarts abruptly. Now uses 3-second timeout instead of immediate ownership.
- **Diagnostic logging not saving**: Fixed issue where log files weren't being saved when installed in `C:\Program Files (x86)\`. Now falls back to `%APPDATA%\RDPClipGuard\` automatically.
- **Clipboard deadlock on file copy**: Fixed 4-5 minute freeze when copying files. Now detects and skips file drops (CF_HDROP) to prevent clipboard lock.
- **Clipboard deadlock on rdpclip restart**: Fixed deadlock when rdpclip.exe is restarting. Now verifies clipboard is actually accessible (not just process.Responding).

### Added
- **Diagnostic Mode improvements**: Now shows actual log file path in balloon notification
- **Clipboard accessibility verification**: Added `CanAccessClipboard()` check to ensure rdpclip is fully operational
- **File drop detection**: Added `IsFileDropClipboard()` to safely skip complex clipboard formats

### Changed
- **Version bump**: 2.0.0 → 2.0.1
- **Installer version**: Updated to 2.0.1
- **Documentation**: Enhanced README with Diagnostic Mode details

---

## [2.0.0] - 2026-03-03

### Added
- **Diagnostic Mode**: Detailed clipboard activity logging
  - Real-time event-based monitoring with WM_CLIPBOARDUPDATE
  - Sequence number tracking for LOCAL/REMOTE synchronization
  - rdpclip.exe health monitoring (PID, memory, handles, responsiveness)
  - Hash comparison between LOCAL and REMOTE clipboard contents
  - Automatic log rotation (keeps 5 most recent logs)
  - Logs saved to `%APPDATA%\RDPClipGuard\RDPClipGuard_Diagnostics_*.log`

- **System Tray context menu enhancements**:
  - "🔍 Diagnostic Mode" toggle
  - "Open Log File" - view latest diagnostic log
  - "Open Log Folder" - browse all logs

- **Auto-start with Windows**: Registry-based auto-start via Run key

### Changed
- **Clipboard monitoring**: Added event-based monitoring (WM_CLIPBOARDUPDATE) alongside polling
- **rdpclip reset logic**: Now auto-resets after 7 copies (previously hardcoded)

### Fixed
- Initial implementation of core features

---

## [1.0.0] - 2026-03-01

### Added
- Initial release
- System tray application for RDP clipboard management
- Automatic rdpclip.exe reset on clipboard failure
- Polling-based clipboard monitoring (2-second interval)
- Windows Forms UI with system tray integration
- Manual reset option via "Reset rdpclip Now"
- Copy counter display in tray tooltip

---

## Notes

### Compatibility
- Windows 10/11 only
- Requires .NET 8 Runtime (self-contained in installer)
- Administrator privileges required

### Known Issues
- File operations with very large files (>100MB) may cause temporary freeze
- RDP session must use `rdpclip.exe` (some RDP clients may use alternatives)

### Testing Notes for 2.0.1
- Tested with local clipboard operations (text copy/paste)
- Tested with file operations (copy/paste files)
- Tested rdpclip auto-reset cycle
- Verified diagnostic logging in both LOCAL and REMOTE sessions
- Verified no "Already running" error on machine restart
