; ─────────────────────────────────────────────────────────────────────────────
; League Login  —  Inno Setup 6 installer script
;
; Prerequisites
;   1. Install Inno Setup 6:  https://jrsoftware.org/isinfo.php
;   2. Build the self-contained exe first:
;        dotnet publish LeagueLogin.csproj -c Release -r win-x64 ^
;               --self-contained true -p:PublishSingleFile=true  ^
;               -p:DebugType=none -p:DebugSymbols=false -o publish
;   3. Run this script in Inno Setup Compiler (or use build-installer.ps1).
;
; The resulting setup installs a single ~60 MB self-contained exe that needs
; no .NET SDK, runtime, or redistributable on the target machine.
; ─────────────────────────────────────────────────────────────────────────────

#define MyAppName    "League Login"
#define MyAppVersion "1.1.1"
#define MyAppURL     "https://github.com/jduust/LeagueLogin"
#define MyAppExeName "LeagueLogin.exe"

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=LeagueLogin
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases

; Stable AppId — keeps future upgrades recognising previous installs.
; (Changing this would make the installer treat an upgrade as a side-by-side install.)
AppId={{3F4A91C2-6B8D-4E5A-9E1C-8F4B2D6A7C11}

; Install under C:\Program Files\LeagueLogin for all users
DefaultDirName={autopf}\LeagueLogin
DefaultGroupName={#MyAppName}
PrivilegesRequired=admin
DisableProgramGroupPage=yes

; Single-file output
OutputDir=installer-output
OutputBaseFilename=LeagueLogin-Setup-{#MyAppVersion}

; Compact LZMA2 compression — produces the smallest possible installer
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Visual settings
WizardStyle=modern
SetupIconFile=Assets\icon.ico
UninstallDisplayIcon={app}\LeagueLogin.exe
UninstallDisplayName={#MyAppName}

; x64 only (matches the publish profile)
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

; Prevent running the installer twice simultaneously
AppMutex=LeagueLoginSetupMutex

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; \
  Description: "Create a &desktop shortcut"; \
  GroupDescription: "Additional shortcuts:"; \
  Flags: checkedonce

Name: "startupicon"; \
  Description: "Start {#MyAppName} automatically when Windows starts"; \
  GroupDescription: "Startup:"; \
  Flags: checkedonce

[Files]
; The single self-contained exe produced by dotnet publish
Source: "publish\LeagueLogin.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Start menu (always)
Name: "{group}\{#MyAppName}";            Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}";  Filename: "{uninstallexe}"

; Desktop shortcut (opt-in via task)
Name: "{userdesktop}\{#MyAppName}"; \
  Filename: "{app}\{#MyAppExeName}"; \
  Tasks: desktopicon

; Run at Windows startup (opt-in via task)
Name: "{userstartup}\{#MyAppName}"; \
  Filename: "{app}\{#MyAppExeName}"; \
  Tasks: startupicon

[Run]
; Offer to launch after install
Filename: "{app}\{#MyAppExeName}"; \
  Description: "Launch {#MyAppName} now"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Kill the app before uninstalling so file removal doesn't fail.
Filename: "{cmd}"; \
  Parameters: "/c taskkill /f /im {#MyAppExeName}"; \
  Flags: runhidden

; NOTE: We deliberately do NOT delete %LocalAppData%\LeagueLogin on uninstall.
; That folder holds settings.json (preferences) and accounts.json (launch
; history). Users who reinstall expect their setup to carry over. Credentials
; live in the Windows Credential Manager and survive uninstall regardless.
; If you want a truly clean uninstall, delete that folder manually.

[Code]
// Close a running instance before upgrading
function InitializeSetup(): Boolean;
var
  Hwnd: HWND;
begin
  Result := True;
  Hwnd   := FindWindowByClassName('LeagueLoginMainWindow');
  if Hwnd <> 0 then
    SendMessage(Hwnd, 16 {WM_CLOSE}, 0, 0);
end;
