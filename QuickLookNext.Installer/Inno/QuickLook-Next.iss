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
