; Inno Setup script for Windows TopBar (framework-dependent)
; Requires .NET 8 Desktop Runtime on the target machine
; Compile with: "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" TopBar-installer.iss

#define MyAppName "Windows TopBar"
#define MyAppVersion "1.0"
#define MyAppPublisher "ThatAerospaceandTechGuy"
#define MyAppURL "https://github.com/ThatAerospaceandTechGuy/Windows-Topbar"
#define MyAppExeName "TopBar.exe"
#define DotNetDownloadURL "https://dotnet.microsoft.com/en-us/download/dotnet/8.0"

[Setup]
AppId={{B597C7F1-0C3F-B307-EC55-B18AC0EDC138}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={commonpf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=LICENSE
OutputDir=.
OutputBaseFilename=TopBar-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "bin\Release\net8.0-windows10.0.17763.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
function IsDotNet8Installed(): Boolean;
var
  Version: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', 'Version', Version) then
  begin
    if (Version <> '') and (Copy(Version, 1, 2) = '8.') then
    begin
      Result := True;
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDotNet8Installed() then
  begin
    if MsgBox('.NET 8 Desktop Runtime is required to run Windows TopBar but was not found on this system.' + #13#10 + #13#10 +
              'Would you like to open the Microsoft download page to install it now?' + #13#10 + #13#10 +
              'After installing .NET 8, please run this installer again.', mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', '{#DotNetDownloadURL}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
    Result := False;
  end;
end;
