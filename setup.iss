; =============================================================================
; Cortex FX — Inno Setup script (v1.6.0+)
;
; Do NOT compile this alone unless Publish\CortexFX_v{version}\ already exists.
; Preferred:  powershell -ExecutionPolicy Bypass -File .\build.ps1
; That publishes the app, then calls ISCC with the correct paths.
; =============================================================================

#define MyAppName "Cortex FX"
#define MyAppPublisher "Cortex FX"
#define MyAppExeName "CortexFX.exe"
#define MyScriptDir AddBackslash(SourcePath)

#ifndef MyAppVersion
  #define MyAppVersion "1.6.0"
#endif

#ifndef MyBuildPath
  #define MyBuildPath MyScriptDir + "Publish\CortexFX_v" + MyAppVersion
#endif

#ifndef MyOutputDir
  #define MyOutputDir MyScriptDir + "Publish"
#endif

; Fail early with a clear message instead of "No files found matching ..."
#if !FileExists(AddBackslash(MyBuildPath) + MyAppExeName)
  #error Publish output not found. Expected CortexFX.exe under Publish\CortexFX_v{#MyAppVersion}\. Run from the project root: powershell -ExecutionPolicy Bypass -File .\build.ps1
#endif

#if !FileExists(MyScriptDir + "Assets\logo.ico")
  #error Missing Assets\logo.ico next to setup.iss
#endif

[Setup]
AppId={{8E863C46-53BE-4B71-8304-C1728E6272B0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
UsedUserAreasWarning=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#MyOutputDir}
OutputBaseFilename=CortexFX_Setup_v{#MyAppVersion}
SetupIconFile={#MyScriptDir}Assets\logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; App binaries + Resources (ffmpeg, magick, …) from the publish folder
Source: "{#MyBuildPath}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
