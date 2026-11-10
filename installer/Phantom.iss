; Inno Setup script for Phantom.
; Build with build\publish.ps1, or manually:
;   dotnet publish src\Phantom.App -c Release -r win-x64 --self-contained true -o artifacts\publish\win-x64
;   iscc /DAppVersion=0.1.0 installer\Phantom.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{6F0C1E0B-6A0B-4E5B-9C3D-5B7A2F4D9E11}
AppName=Phantom
AppVersion={#AppVersion}
AppVerName=Phantom {#AppVersion}
AppPublisher=Project Phantom
DefaultDirName={autopf}\Phantom
DefaultGroupName=Phantom
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Phantom.exe
SetupIconFile=..\src\Phantom.App\Assets\phantom.ico
OutputDir=..\artifacts\installer
OutputBaseFilename=Phantom-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Phantom never needs administrator rights, and neither does installing it:
; the default is a per-user install. "Install for all users" remains available.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
CloseApplications=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=Phantom

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Phantom"; Filename: "{app}\Phantom.exe"
Name: "{autodesktop}\Phantom"; Filename: "{app}\Phantom.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Phantom.exe"; Description: "{cm:LaunchProgram,Phantom}"; Flags: nowait postinstall skipifsilent

[Code]
// Settings, history and logs live in the user's profile, outside the install folder.
// Ask before removing them so an uninstall never silently destroys recorded history.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Phantom');
    if DirExists(DataDir) and not UninstallSilent then
    begin
      if MsgBox('Also delete Phantom''s settings, logs and recorded history?' + #13#10 + #13#10 + DataDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
