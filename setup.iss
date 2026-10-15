; Installer for RDP Shadow. Run .\build.ps1 first (fills dist\), then:
;   & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" setup.iss   → installer\RdpShadow-Setup-<ver>.exe

#define AppName "RDP Shadow"
#define AppVersion "1.0.0"
#define AppExe "RdpShadow.App.exe"

[Setup]
AppId={{0F7013EF-07ED-4BD1-AD23-DD10E8271C7D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=yehuda-menachem
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=src\RdpShadow.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=installer
OutputBaseFilename=RdpShadow-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "he"; MessagesFile: "compiler:Languages\Hebrew.isl"

[CustomMessages]
he.NeedDotNet=האפליקציה דורשת את ‎.NET 10 Desktop Runtime (x64), והוא לא מותקן במחשב הזה.%n%nלפתוח את דף ההורדה? אחרי ההתקנה שלו יש להריץ את ההתקנה הזו שוב.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; ponytail: source timestamps are kept (Inno default), and the app's "Update Agent" check compares Agent\RdpShadow.Agent.exe write times
Source: "dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// The app is framework-dependent (WPF): without the .NET 10 Desktop Runtime it doesn't start at all.
// ponytail: checks the default dotnet location only; a custom DOTNET_ROOT install is reported as missing.
function InitializeSetup(): Boolean;
var
  F: TFindRec;
  ErrorCode: Integer;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), F);
  if Result then
    FindClose(F)
  else if SuppressibleMsgBox(CustomMessage('NeedDotNet'), mbError, MB_YESNO, IDNO) = IDYES then
    ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;
