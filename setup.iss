; Cortex FX installer script.
; Paths are resolved relative to this script unless build.ps1 passes overrides.

#define MyAppName "Cortex FX"
#define MyAppPublisher "Cortex FX"
#define MyAppExeName "CortexFX.exe"
#define MyScriptDir AddBackslash(SourcePath)

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef MyBuildPath
  #define MyBuildPath MyScriptDir + "Publish\CortexFX_v" + MyAppVersion
#endif

#ifndef MyOutputDir
  #define MyOutputDir MyScriptDir + "Publish"
#endif

[Setup]
AppId={{8E863C46-53BE-4B71-8304-C1728E6272B0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
UsedUserAreasWarning=no
PrivilegesRequired=lowest
OutputDir={#MyOutputDir}
OutputBaseFilename=CortexFX_Setup_v{#MyAppVersion}
SetupIconFile={#MyScriptDir}logo.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyBuildPath}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
