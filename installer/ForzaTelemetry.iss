; Forza Telemetry - per-machine installer (Inno Setup 6.3 or later).
; Compiled by installer\build.ps1, which passes StageDir (the self-contained publish output), AppVersion and OutputDir.
; Keep the CustomMessages ASCII-only (or save this file as UTF-8 with a BOM) so the Spanish text reads correctly.
;
; Renaming the product: change the defines below (and AppId must stay the same so upgrades keep working).

#if Ver < EncodeVer(6,3,0)
  #error Inno Setup 6.3 or later is required (x64compatible)
#endif
#ifndef StageDir
  #error StageDir must be passed by build.ps1 (/DStageDir=...)
#endif
#ifndef AppVersion
  #error AppVersion must be passed by build.ps1 (/DAppVersion=x.y.z)
#endif
#ifndef OutputDir
  #error OutputDir must be passed by build.ps1 (/DOutputDir=...)
#endif

; ---- single place to rename the product ----
#define AppName      "Forza Telemetry"
#define AppPublisher "<COPYRIGHT HOLDER>"
#define AppExe       "ForzaTelemetry.App.exe"
#define AppUrl       "https://github.com/<OWNER>/<REPO>"
#define FirewallRule "Forza Telemetry (UDP 5300)"
#define DataOutPort  "5300"

[Setup]
; Fixed once. Never change it, or upgrades will install side by side instead of replacing.
AppId={{B7E4A1D2-3C58-4F6A-9D21-5E8A0C7F1B64}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Admin is required: the firewall rule below is machine-wide.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=ForzaTelemetry-Setup-{#AppVersion}
SetupIconFile=assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
WizardStyle=modern
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage
; Spanish for a Spanish UI language; otherwise the first [Languages] entry (English) is the fallback.

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
en.UserDataKept=Your settings, recordings and logs were kept in your user profile (under %%APPDATA%%\ForzaTelemetry and %%LOCALAPPDATA%%\ForzaTelemetry). The uninstaller does not remove them. Delete those folders by hand if you no longer need them.
es.UserDataKept=Sus ajustes, grabaciones y registros se conservaron en su perfil de usuario (en %%APPDATA%%\ForzaTelemetry y %%LOCALAPPDATA%%\ForzaTelemetry). El desinstalador no los elimina. Elimine esas carpetas manualmente si ya no los necesita.
en.FirewallGroup=Network:
es.FirewallGroup=Red:
en.FirewallTask=Allow Forza Telemetry through Windows Firewall (UDP port {#DataOutPort}, private networks)
es.FirewallTask=Permitir Forza Telemetry en el Firewall de Windows (puerto UDP {#DataOutPort}, redes privadas)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "firewall"; Description: "{cm:FirewallTask}"; GroupDescription: "{cm:FirewallGroup}"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Why the rule is scoped this way: the game (or an Xbox) sends UDP to this PC, so Windows needs an INBOUND allow rule.
;  - program=...: only this executable may receive on the port, not every program on the machine.
;  - profile=private: it is open on home/trusted networks only, never on public Wi-Fi.
;  - localport=5300: just the Data Out port.
; The delete first keeps upgrades and reinstalls from piling up duplicate rules (its exit code is ignored).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow protocol=UDP localport={#DataOutPort} program=""{app}\{#AppExe}"" profile=private"; Flags: runhidden; Tasks: firewall; StatusMsg: "{cm:FirewallTask}"
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; RunOnceId: "DeleteFirewallRule"

[Code]
{ The uninstaller never deletes anything in the user profile (settings, recordings, logs). Say so once, when it ends. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    SuppressibleMsgBox(CustomMessage('UserDataKept'), mbInformation, MB_OK, IDOK);
end;
