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

[Setup]
AppName=League Login
AppVersion=1.0.2
AppPublisher=LeagueLogin
AppPublisherURL=https://github.com/jduust/LeagueLogin
AppSupportURL=https://github.com/jduust/LeagueLogin
AppUpdatesURL=https://github.com/jduust/LeagueLogin

; Install under C:\Program Files\LeagueLogin for all users
DefaultDirName={autopf}\LeagueLogin
DefaultGroupName=League Login
PrivilegesRequired=admin

; Single-file output
OutputDir=installer-output
OutputBaseFilename=LeagueLogin-Setup-1.0.0

; Compact LZMA2 compression — produces the smallest possible installer
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Visual settings
WizardStyle=modern
SetupIconFile=Assets\icon.ico    ; optional — remove this line if you have no icon
UninstallDisplayIcon={app}\LeagueLogin.exe

; x64 only (matches the publish profile)
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

; Prevent running the installer twice simultaneously
AppMutex=LeagueLoginSetupMutex

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; The single self-contained exe produced by dotnet publish
Source: "publish\LeagueLogin.exe"; DestDir: "{app}"; Flags: ignoreversion

; Optional: ship an icon file for the tray (remove if not using one)
; Source: "Assets\icon.ico"; DestDir: "{app}\Assets"; Flags: ignoreversion

[Icons]
; Start menu
Name: "{group}\League Login";         Filename: "{app}\LeagueLogin.exe"
Name: "{group}\Uninstall League Login"; Filename: "{uninstallexe}"

; Desktop shortcut
Name: "{userdesktop}\League Login";   Filename: "{app}\LeagueLogin.exe"

; Run at Windows startup (optional — comment out if not wanted)
; Name: "{userstartup}\League Login"; Filename: "{app}\LeagueLogin.exe"

[Run]
; Offer to launch after install
Filename: "{app}\LeagueLogin.exe";
  Description: "Launch League Login now";
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Kill the app before uninstalling
Filename: "{cmd}";
  Parameters: "/c taskkill /f /im LeagueLogin.exe";
  Flags: runhidden

[UninstallDelete]
; Remove the log folder from AppData on uninstall
Type: filesandordirs;
  Name: "{localappdata}\LeagueLogin"

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
