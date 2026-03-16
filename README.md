# League Login

A lightweight Windows tray app for switching between League of Legends accounts. Stores credentials securely in the Windows Credential Manager (DPAPI-encrypted, never plain text) and automates the Riot Client login using UI Automation.

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

[**LeagueLogin-1.0.2-x64.msi**](https://github.com/jduust/LeagueLogin/releases/download/v1.0.2/LeagueLogin-1.0.2-x64.msi)

Requires Windows 10 or later, x64. No .NET runtime required — it is bundled inside the installer.

> **Windows SmartScreen warning:** When you run the installer, Windows may show a blue "Windows protected your PC" dialog. Click **More info → Run anyway**. This happens because the app is new and unsigned — it is not malicious. The warning will go away as the app accumulates download history.

---

## Features

- Saves accounts in **Windows Credential Manager** — the same store used by Windows itself
- One click to kill existing Riot processes, launch the client, and log in automatically
- **System tray** with right-click menu to switch accounts without opening the window
- **Windows taskbar Jump List** — right-click the taskbar button to launch directly as any account
- Window auto-sizes to fit your account list
- Custom borderless window chrome
- Logs written to `%LocalAppData%\LeagueLogin\debug.log` (tray → View Logs)

---

## Building from source

### Dependencies

| Dependency | Version | Link |
|---|---|---|
| .NET SDK | 8.0 | [dotnet.microsoft.com](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) |
| WiX Toolset *(MSI only)* | 6.x | `dotnet tool install --global wix` |
| WiX UI extension *(MSI only)* | 6.0.2 | `wix extension add --global WixToolset.UI.wixext/6.0.2` |

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

### Build the MSI installer

One-time setup:

```powershell
dotnet tool install --global wix
wix extension add --global WixToolset.UI.wixext/6.0.2
```

Then publish first (see above), then:

```powershell
wix build installer.wxs -ext WixToolset.UI.wixext -o LeagueLogin-1.0.0-x64.msi
```

Or use the helper script which does both steps:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\build-installer.ps1
```

The installer lets you choose:
- Desktop shortcut (optional, on by default)
- Run at Windows login (optional, on by default)

---

### Logs

If something is not working, check the log file at:

```
%LocalAppData%\LeagueLogin\debug.log
```

Or open it from the tray icon → **View Logs**.

---

## How it works

1. Kills any running Riot Client / League processes
2. Launches `RiotClientServices.exe` with the `--force-renderer-accessibility` flag (required for UI Automation)
3. Uses [FlaUI](https://github.com/FlaUI/FlaUI) to find the login window and fill in the username and password fields
4. Clicks the sign-in button and waits for the login fields to disappear as confirmation

The sign-in button is located heuristically — it finds the button whose accessibility `DefaultAction` appears the fewest times among all buttons on the page, which reliably identifies the unique submit button regardless of the exact UI layout Riot is showing.

---

## Credential storage

Credentials are stored under `LeagueLogin_<label>` in Windows Credential Manager (`CRED_TYPE_GENERIC`, `CRED_PERSIST_LOCAL_MACHINE`). They are encrypted by Windows using DPAPI and are tied to the machine. You can view or delete them at any time via **Control Panel → Credential Manager → Windows Credentials**.

---

## License

MIT
