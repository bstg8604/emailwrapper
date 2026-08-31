; Inno Setup script for Purplemail.
;
; Build with:  iscc build\Purplemail.iss   (after build\publish.ps1 has produced the payload)
; Or just run: powershell -File build\publish.ps1   which does both.
;
; Why an installer rather than handing over the published .exe: a bare exe has no Start Menu
; entry, no uninstall entry, no fixed install location for the "start with Windows" registry
; value to point at, and no upgrade story — every one of those is what makes an app feel like a
; real product rather than a download.
;
; The payload is a self-contained publish, so the .NET runtime travels with the app and the only
; external dependency left is the WebView2 runtime (checked for below).

#define AppName        "Purplemail"
#define AppVersion     "1.0.4"
#define AppPublisher   "Purplemail"
#define AppExe         "Purplemail.exe"
#define SourceDir      "..\dist\publish"

[Setup]
; A fixed GUID is what lets a later version recognise and upgrade this install instead of
; landing beside it as a second copy. Never change it between releases.
AppId={{8E4C1F27-6A3B-4D59-9C2E-51A7F0B4D8E3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
; Otherwise the Setup.exe wrapper itself carries no version info at all (blank on its own
; Properties dialog) — this is separate from the app's own exe, which gets its version from the
; csproj instead.
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoDescription=Purplemail Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\dist
OutputBaseFilename=Purplemail-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Per-user install: no UAC prompt, and it matches the app's per-user settings, DPAPI-encrypted
; credentials and HKCU startup entry. A machine-wide install would need elevation for no benefit.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
SetupIconFile=..\src\EmailClient\icon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "startupicon"; Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Matches Settings/StartupRegistration.cs exactly — same key, same value name, same --tray flag —
; so the in-app checkbox and this task control one setting rather than two competing ones.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "Purplemail"; ValueData: """{app}\{#AppExe}"" --tray"; \
    Flags: uninsdeletevalue; Tasks: startupicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The Run entry is removed by uninstall via uninsdeletevalue above. Deliberately NOT deleting
; %LOCALAPPDATA%\IITBWebmailWrapper — that holds the user's settings, signatures, contacts and
; saved account. An uninstall that silently destroys those is a bad surprise on a reinstall.
Type: dirifempty; Name: "{app}"

[Code]
// WebView2 renders the reading pane, the compose editor and the signature editor. Without its
// runtime the app starts but those surfaces fail, which is a confusing way to discover a missing
// dependency — so check up front. Windows 11 ships it; older Windows 10 may not.
function WebView2Installed(): Boolean;
var
  Value: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Value) or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Value) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Value);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not WebView2Installed() then
    if MsgBox('Purplemail needs the Microsoft Edge WebView2 runtime to display messages.' + #13#10#13#10 +
              'It was not found on this PC. You can install Purplemail now and add WebView2 afterwards ' +
              'from https://go.microsoft.com/fwlink/p/?LinkId=2124703' + #13#10#13#10 +
              'Continue with the installation?', mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
end;
