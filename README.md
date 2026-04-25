# League Login

A lightweight Windows tray app for switching between League of Legends accounts. Stores credentials securely in the Windows Credential Manager (DPAPI-encrypted, never plain text) and automates the Riot Client login using UI Automation — entirely in the background, no window stealing.

<img width="360" height="519" alt="billede" src="https://github.com/user-attachments/assets/13e6afac-d46c-47b9-85fb-f842468a7055" />
<img width="320" height="390" alt="billede" src="https://github.com/user-attachments/assets/5226e555-f6bf-47ef-b759-55a29d1ee52b" />

---

## Trust & Security

> **Building from source is strongly recommended.**

This app stores your Riot account passwords. You should never run arbitrary executables from the internet that handle credentials — including this one — without reviewing the source first.

The code is all here. It is a few hundred lines of straightforward C# WPF. Read it, build it yourself, and know exactly what is running on your machine.

A pre-built installer is provided below for convenience, but **the author makes no guarantees and you use it at your own risk.**

---

## Download (pre-built)

[**LeagueLogin-1.1.1-x64.msi**](https://github.com/jduust/LeagueLogin/releases/download/v1.1.1/LeagueLogin-1.1.1-x64.msi)

Requires Windows 10 or later, x64. No .NET runtime required — it is bundled inside the installer.

> **Windows SmartScreen warning:** When you run the installer, Windows may show a blue "Windows protected your PC" dialog. Click **More info → Run anyway**. This happens because the app is new and unsigned — it is not malicious. The warning will go away as the app accumulates download history.

---

## Features

### Account management
- Saves accounts in **Windows Credential Manager** (DPAPI-encrypted, same store Windows itself uses)
- **Star a preferred account** — pinned to the top of the list, used for boot auto-login
- **Smart sort order** — preferred first, then most-recently-used, then alphabetical
- **Per-account usage info** — "Launched 2 hr ago · 17 total" subtitle
- **Right-click / overflow menu** on each row: Launch · Copy username · Set preferred · Edit · Remove
- Rename-safe edits — usage history and preferred-account pointer follow the rename

### Login automation
- One click to kill existing Riot processes, launch the client, and log in automatically
- **Background-only** — uses UI Automation to fill fields and posts Enter via `PostMessage`. The Riot Client never gets foregrounded, so you can keep working while it logs in
- Patch-aware — if a patch is required, clicks Update automatically, waits it out, then proceeds to launch
- Verifies `LeagueClient.exe` actually started after clicking Play; re-clicks Play if the first click didn't register
- Diagnostic UI tree dumps to your desktop on patch detection or unexpected timeouts (great for filing bugs)

### Boot auto-login
- Settings → "Auto-login to preferred account on boot"
- Adds a `--boot-login` flag to the Windows startup entry
- Logs into your preferred account automatically when you sign into Windows
- Skipped automatically if Riot/League is already running (won't interrupt an active session)

### System integration
- **System tray** with right-click menu to switch accounts without opening the window
- **Windows taskbar Jump List** — right-click the taskbar button to launch directly as any account
- Window auto-sizes to fit your account list
- Custom borderless window chrome
- Auto-detects the Riot Client installation (works across drives and custom install paths)
- Logs written to `%LocalAppData%\LeagueLogin\debug.log` (tray → View Logs)

### Updates
- **Built-in update check** against GitHub Releases on startup (once per day, can be disabled)
- In-app banner offers Download & install · Release notes · Skip this version
- Downloads the MSI and runs it via `msiexec /passive` — minimal interruption
- Manual "Check now" button in Settings

---

## Building from source

### Dependencies

| Dependency | Version | Link |
|---|---|---|
| .NET SDK | 8.0 | [dotnet.microsoft.com](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) |
| WiX Toolset *(MSI only)* | 6.x | `dotnet tool install --global wix` |
| WiX UI extension *(MSI only)* | 6.0.2 | `wix extension add --global WixToolset.UI.wixext/6.0.2` |
| Inno Setup *(optional)* | 6.x | [jrsoftware.org/isinfo.php](https://jrsoftware.org/isinfo.php) |

Clone the repo and `cd` into the project folder, then follow the steps below.

---

### Run (development)

```powershell
dotnet run
```

---

### Build (debug)

```powershell
dotnet build
```

The output lands in `bin\Debug\net8.0-windows\`.

---

### Publish (self-contained exe + DLLs)

```powershell
dotnet publish LeagueLogin.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o publish
```

The `publish\` folder contains the exe and a handful of native DLLs (FlaUI UI Automation interop). **All of them are required** — copy the whole folder, not just the exe.

---

### Build the installer

One-time setup:

```powershell
dotnet tool install --global wix
wix extension add --global WixToolset.UI.wixext/6.0.2
```

Then publish first (see above), then:

```powershell
wix build installer.wxs -ext WixToolset.UI.wixext -o LeagueLogin-1.1.1-x64.msi
```

Or use the helper script which builds the exe, the MSI, and (if Inno Setup is installed) the `.exe` setup in one go:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\build-installer.ps1
```

Flags: `-ExeOnly` (skip installers), `-NoInno` (MSI only), `-NoMsi` (Inno only).

The MSI installer offers:
- Desktop shortcut (optional, on by default)
- Run at Windows login (optional, on by default)
- Launch League Login now (Finish dialog checkbox)

---

### Logs

If something is not working, check the log file at:

```
%LocalAppData%\LeagueLogin\debug.log
```

Or open it from the tray icon → **View Logs**.

When the app gets stuck during a patch or can't find the Play button, it dumps the entire Riot Client UI tree to your desktop as `LeagueLogin-ui-<reason>-<timestamp>.txt`. Attach that file to bug reports — it makes diagnosis trivial.

---

## How it works

1. Kills any running Riot Client / League processes
2. Launches `RiotClientServices.exe` (located via Riot's own `RiotClientInstalls.json` manifest, falling back to drive scanning) with the `--force-renderer-accessibility` flag — required for UI Automation
3. Waits for the login fields to appear, fills them via `ValuePattern` (no keyboard required, no foreground stealing)
4. Submits via `PostMessage(WM_KEYDOWN/WM_CHAR/WM_KEYUP, VK_RETURN)` to the focused Chromium widget
5. After the client lands on the home screen, classifies the launch area: **Play** → click and verify `LeagueClient.exe` starts; **Update/Install** → click and enter patch-wait mode; **patch in progress** → just wait, log progress, then click Play when it appears

The sign-in button is located heuristically — it finds the unique nameless button with a `DefaultAction`, which reliably identifies the submit button regardless of the exact UI layout Riot is showing.

---

## Credential storage

Credentials are stored under `LeagueLogin_<label>` in Windows Credential Manager (`CRED_TYPE_GENERIC`, `CRED_PERSIST_LOCAL_MACHINE`). They are encrypted by Windows using DPAPI and are tied to the machine. You can view or delete them at any time via **Control Panel → Credential Manager → Windows Credentials**.

Non-sensitive metadata (last-used timestamps, launch counts, preferred-account name) lives in `%LocalAppData%\LeagueLogin\accounts.json` and `settings.json`. These are preserved across uninstall/reinstall so your setup carries over.

---

## License

MIT
