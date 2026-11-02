#ifndef PackageRoot
  #error PackageRoot must point to a self-contained package
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{8F864D31-7AD4-4FF9-8513-D749B746E6AA}
AppName=Monitor Remote
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Monitor Remote
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UninstallDisplayIcon={app}\MonitorRemote.Desktop.exe
SetupIconFile=..\desktop\assets\monitor-remote.ico
OutputDir=..\artifacts
OutputBaseFilename=MonitorRemote-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PackageRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Monitor Remote"; Filename: "{app}\MonitorRemote.Desktop.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Monitor Remote"; Filename: "{app}\MonitorRemote.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\MonitorRemote.Desktop.exe"; Description: "Launch Monitor Remote"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MonitorRemote', StartupCommand) then
      if CompareText(StartupCommand, '"' + ExpandConstant('{app}\MonitorRemote.Desktop.exe') + '"') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MonitorRemote');
end;
