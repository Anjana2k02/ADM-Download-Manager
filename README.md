# ADM — your personal download manager

ADM is a native Windows download manager built with C# and WPF on .NET 10. It has an original interface and branding, with familiar IDM-style download controls. No Electron, embedded browser, third-party runtime packages, accounts, advertising, or telemetry.

## Run ADM

For automatic GitHub releases and an installer on every push to `main`, follow [GITHUB-RELEASES.md](GITHUB-RELEASES.md).

- **On this PC:** open `artifacts/ADM-portable/ADM.exe`. The .NET 10 Desktop Runtime is already installed here.
- **On another Windows x64 PC:** extract `artifacts/ADM-standalone-win-x64.zip` and open `ADM.exe`. This package includes its runtime.
- **Compact distribution:** `artifacts/ADM-portable-win-x64.zip` requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Choose **Desktop Runtime**, Windows x64.

No administrator privileges are needed. Keep the browser helper files together with ADM if you use the extension. These locally built executables are unsigned; a production release would need signing and wider compatibility testing.

## Included

| Feature | Behavior |
| --- | --- |
| HTTP/HTTPS downloads | Direct file URLs, redirects, unknown-length responses, empty files |
| Download acceleration | 1–16 fixed parallel segments for range-capable files of at least 2 MiB |
| Pause and resume | Saves partial files; resumes with matching strong ETag or Last-Modified and file length |
| Safe fallback | Restarts partial data when the server cannot safely support resume |
| Automatic categories | Documents, Video, Music, Programs, Archives, Images, Other |
| Destination control | Default library folder, custom destination, optional custom filename |
| Server filenames | Reads Content-Disposition; recalculates automatic category before transfer |
| Queue | 1–8 concurrent downloads, individual controls, pause all, saved history |
| Scheduling | Per-download start date and local time; ADM must be running and the computer awake |
| Speed controls | Live aggregate and individual caps in KiB/s; 0 means unlimited |
| Batch entry | One URL per line, with duplicate active URL detection |
| Verification | SHA-256 calculated on every completed download; optional expected hash |
| Reliability | Timeouts, bounded exponential retries, persisted part maps, atomic queue save plus backup |
| File protection | Sanitized names, no overwriting existing files, size and range validation |
| Windows integration | Tray operation, completion notification, Explorer and file opening |
| Browser helper | Optional Chrome/Edge Manifest V3 extension sends direct links to ADM |
| Library | Category/status filters, search, progress, speed, ETA, file details |

## Everyday use

1. Choose **New download** or **Paste link**. Paste a direct file URL. For a batch, use one URL per line.
2. Leave **Save to** blank to use automatic category folders. Set it to a folder to use that exact location.
3. Leave **File name** blank to use a filename from the URL or server. A custom name overrides the server.
4. Select a row to pause or resume it. Use **···** for opening, copying the URL/checksum, individual speed limits, restarting, and removing history.
5. Use **Preferences** for the default folder, categories, connection counts, concurrency, total speed limit, retries, and tray behavior.

**Minimize** keeps transfers running, optionally in the tray. **Close** pauses active work, saves state, and exits. Reopening restores interrupted work as paused; future scheduled work retains its schedule. **Pause all** also pauses queued and scheduled work. Completed files remain on disk when removed from the history.

Keyboard shortcuts: **Ctrl+N** new download, **Ctrl+V** paste a link when focus is outside a text box, **Ctrl+F** search.

Browser setup: see [BROWSER-SETUP.md](BROWSER-SETUP.md). The extension is not installed or registered automatically.

## Storage and resource use

- Settings and queue: `%LocalAppData%\ADM\downloads.json` with a `.bak` backup.
- Default library: `%UserProfile%\Downloads\ADM\[Category]`.
- Temporary parts: `<destination>\.adm-parts\<download-id>`. Do not edit them while ADM is running.
- Assembly temporarily needs approximately twice the file size on the destination volume. Parts are removed after successful completion or after removing an unfinished download.
- URLs are stored locally in the queue. Signed URLs can contain sensitive tokens, so avoid sharing the queue file.
- Completed downloads receive the Windows Internet Zone marker on filesystems that support alternate data streams.
- The compact build reuses the installed .NET runtime. The standalone build is larger because it includes that runtime. Transfer buffering is bounded; file contents are streamed to disk.

## Scope and practical limits

This is an independently built ADM 1.0, **not a 100% replica of IDM**. It does not include automatic interception of all browser downloads, browser-cookie import, login-page handling, HLS/DASH video assembly, DRM streaming, site crawling, FTP, torrents, recurring schedules, wake-from-sleep, or self-updates. The optional extension transfers a direct link to the app; it does not extract video from webpages or share browser sessions.

Multiple connections can improve throughput when a server permits it; they cannot exceed the network or server's available bandwidth. Some servers restrict range requests or require browser-only authentication. Use direct download links; expired signed links need to be added again with a fresh URL. A changed file or unsafe range response is never silently stitched into existing data. Last-Modified validation relies on the server reporting truthful timestamps. Without a trustworthy validator, ADM restarts on resume.

Only Windows x64 is packaged and tested here. No claim of bug-free operation or complete IDM feature parity is made. The browser extension's handlers and native-message framing are tested locally; installation and end-to-end behavior in a real Chrome/Edge profile still need validation.

## Build and test

Requirements: Windows and .NET 10 SDK. No NuGet application dependencies are needed; publishing may download Microsoft's runtime packs.

```powershell
dotnet build ADM.sln -c Release
dotnet run --project tests/ADM.Tests -c Release
node tests/browser-helper.test.cjs
python tests/test_bridge.py
powershell -File scripts/Build.ps1
powershell -File scripts/Build.ps1 -Standalone
```

The integration suite uses a real loopback HTTP server and deterministic files. It verifies segmented output, resume, process-lifecycle persistence, redirects, no-range and unknown-size servers, server filenames, hashes, retries, throttling, schedules, concurrency, path collisions, state recovery, and removal behavior.

Visual QA modes (isolated temporary state; preview fixtures never enter the real library):

```powershell
.\artifacts\ADM-portable\ADM.exe --screenshot "$PWD\artifacts\adm-empty.png"
.\artifacts\ADM-portable\ADM.exe --preview --screenshot "$PWD\artifacts\adm-preview.png"
.\artifacts\ADM-portable\ADM.exe --screenshot "$PWD\artifacts\adm-new.png" --capture-dialog new
```

Source layout: `src/ADM.Core` transfer engine; `src/ADM.App` native UI; `src/ADM.Bridge` browser native host; `browser-extension` optional helper; `tests` integration checks; `scripts` build and opt-in setup.

Reference behavior was checked against [IDM's documented controls](https://www.internetdownloadmanager.com/support/using_idm/using_idm.html). The UI uses [Microsoft WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/).
