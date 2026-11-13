#define MyAppName "QuickLook-Next"
#define MyAppPublisher "Adstrax"
#define MyAppExeName "QuickLook-Next.exe"
#define MyInstallerName "QuickLook-Next-Setup"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#ifndef MyAppNumericVersion
  #define MyAppNumericVersion "0.0.0"
#endif

#ifndef SourceDir
  #error SourceDir must point to the staged release package
#endif

#ifndef OutputDir
  #define OutputDir "..\..\Build\Release"
#endif

[Setup]
AppId={{71A7BFE0-84CD-4A31-8D4F-69CE42A88C15}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/Adstrax/Quicklook-Next
AppSupportURL=https://github.com/Adstrax/Quicklook-Next/issues
AppUpdatesURL=https://github.com/Adstrax/Quicklook-Next/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
; Always show the destination page. Inno's "auto" default can hide it during an
; upgrade or when it considers the default path to be the only likely choice.
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#MyInstallerName}-{#MyAppVersion}
SetupIconFile=..\..\QuickLookNext\Resources\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
WizardStyle=modern dynamic
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
AppMutex=QuickLookNext.App.Mutex
UsePreviousAppDir=yes
LicenseFile=..\gpl.rtf
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoVersion={#MyAppNumericVersion}
VersionInfoProductVersion={#MyAppNumericVersion}
VersionInfoOriginalFileName={#MyInstallerName}-{#MyAppVersion}.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "portable.lock"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\portable.lock"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNet11DesktopKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  DotNet11DesktopKey32 = 'SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  DotNet11DownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/11.0';

function HasDotNet11DesktopInKey(const Key: String): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM, Key, Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('11.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function HasDotNet11Desktop: Boolean;
begin
  Result := HasDotNet11DesktopInKey(DotNet11DesktopKey) or
            HasDotNet11DesktopInKey(DotNet11DesktopKey32);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ShellErrorCode: Integer;
begin
  if HasDotNet11Desktop then
  begin
    Result := '';
    Exit;
  end;

  if not WizardSilent then
  begin
    if MsgBox(
      'QuickLook-Next 7 requires the Microsoft .NET 11 Desktop Runtime (x64).' + #13#10 +
      'QuickLook-Next 7 需要 Microsoft .NET 11 桌面运行时（x64）。' + #13#10#13#10 +
      'Open the official download page now? 现在打开官方下载页面吗？',
      mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', DotNet11DownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ShellErrorCode);
  end;

  Result :=
    'Microsoft .NET 11 Desktop Runtime (x64) is required. ' +
    'Install it from https://dotnet.microsoft.com/download/dotnet/11.0 and run Setup again.';
end;
