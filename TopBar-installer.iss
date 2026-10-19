; Inno Setup script for Windows TopBar (framework-dependent)
; Auto-downloads and installs .NET 8 Desktop Runtime if missing
; Compile with: "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" TopBar-installer.iss

#define MyAppName "Windows TopBar"
#define MyAppVersion "1.0"
#define MyAppPublisher "ThatAerospaceandTechGuy"
#define MyAppURL "https://github.com/ThatAerospaceandTechGuy/Windows-Topbar"
#define MyAppExeName "TopBar.exe"
#define DotNetVersion "8.0.31"
#define DotNetDownloadURL "https://dotnetcli.azureedge.net/dotnet/WindowsDesktop/8.0.31/windowsdesktop-runtime-8.0.31-win-x64.exe"
#define DotNetManualURL "https://dotnet.microsoft.com/en-us/download/dotnet/8.0"

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
  DotNetInstallerPath: String;
  ResultCode: Integer;
begin
  Result := True;

  if IsDotNet8Installed() then
  begin
    // .NET 8 is already installed, proceed
    Result := True;
    Exit;
  end;

  // .NET 8 not found - download and install
  if MsgBox('.NET 8 Desktop Runtime is required but was not found on this system.' + #13#10 + #13#10 +
            'The installer will now download and install it automatically from Microsoft.' + #13#10 +
            'This may take a few minutes.', mbInformation, MB_OKCANCEL) <> IDOK then
  begin
    Result := False;
    Exit;
  end;

  DotNetInstallerPath := ExpandConstant('{tmp}\windowsdesktop-runtime-8.0.31-win-x64.exe');

  // Download the .NET 8 Desktop Runtime installer using bitsadmin (built into Windows)
  WizardForm.StatusLabel.Caption := 'Downloading .NET 8 Desktop Runtime...';
  if not Exec('bitsadmin.exe', '/transfer "dotnet8" "{#DotNetDownloadURL}" "' + DotNetInstallerPath + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Failed to download .NET 8 Desktop Runtime.' + #13#10 + #13#10 +
           'Please download and install it manually from:' + #13#10 +
           '{#DotNetManualURL}', mbError, MB_OK);
    ShellExec('open', '{#DotNetManualURL}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    MsgBox('Failed to download .NET 8 Desktop Runtime (exit code: ' + IntToStr(ResultCode) + ').' + #13#10 + #13#10 +
           'Please download and install it manually from:' + #13#10 +
           '{#DotNetManualURL}', mbError, MB_OK);
    ShellExec('open', '{#DotNetManualURL}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
    Exit;
  end;

  // Run the .NET installer silently
  WizardForm.StatusLabel.Caption := 'Installing .NET 8 Desktop Runtime...';
  if not Exec(DotNetInstallerPath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Failed to run the .NET 8 installer.' + #13#10 + #13#10 +
           'Please install it manually from:' + #13#10 +
           '{#DotNetManualURL}', mbError, MB_OK);
    ShellExec('open', '{#DotNetManualURL}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    MsgBox('The .NET 8 installation may not have completed successfully (exit code: ' + IntToStr(ResultCode) + ').' + #13#10 + #13#10 +
           'Please install it manually from:' + #13#10 +
           '{#DotNetManualURL}', mbError, MB_OK);
    ShellExec('open', '{#DotNetManualURL}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
    Exit;
  end;

  // Verify installation succeeded
  if not IsDotNet8Installed() then
  begin
    MsgBox('.NET 8 Desktop Runtime installation did not complete.' + #13#10 + #13#10 +
           'Please install it manually from:' + #13#10 +
           '{#DotNetManualURL}', mbError, MB_OK);
    ShellExec('open', '{#DotNetManualURL}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
    Exit;
  end;

  MsgBox('.NET 8 Desktop Runtime installed successfully.', mbInformation, MB_OK);
  Result := True;
end;
