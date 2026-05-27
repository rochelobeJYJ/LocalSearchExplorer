#define MyAppName "Local File Search Explorer"
#define MyAppExeName "LocalSearch.App.exe"
#include "version.iss.inc"

[Setup]
AppId={{7F56E5B0-9EE9-4BB6-8CF6-A6E0850AA4F6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=LocalSearchExplorer
#if MyGithubRepo != ""
AppPublisherURL=https://github.com/{#MyGithubRepo}
AppSupportURL=https://github.com/{#MyGithubRepo}
AppUpdatesURL=https://github.com/{#MyGithubRepo}/releases
#endif
DefaultDirName={localappdata}\Programs\LocalSearchExplorer
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\artifacts\installer
OutputBaseFilename={#MyInstallerOutputBaseName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
SetupIconFile=..\src\LocalSearch.App\Assets\AppIcon.ico

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{localappdata}\LocalSearchExplorer"
Name: "{localappdata}\LocalSearchExplorer\updates"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon; IconFilename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\LocalSearchExplorer\updates"

[Code]
procedure TryTerminateProcessByImageName(const ImageName: string);
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/F /IM "' + ImageName + '"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  );
end;

procedure CloseLocalSearchProcesses;
begin
  TryTerminateProcessByImageName('{#MyAppExeName}');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    CloseLocalSearchProcesses;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    CloseLocalSearchProcesses;
  end;
end;
