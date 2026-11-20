; =============================================================================
; Cortex FX — Inno Setup script (v1.9.5+)
;
; Do NOT compile this alone unless Publish\CortexFX_v{version}\app\ already exists.
; Preferred:  powershell -ExecutionPolicy Bypass -File .\build.ps1
; That publishes the app, then calls ISCC with the correct paths.
; =============================================================================

#define MyAppName "Cortex FX"
#define MyAppPublisher "Cortex FX"
#define MyAppExeName "CortexFX.exe"
#define MyScriptDir AddBackslash(SourcePath)

#ifndef MyAppVersion
  #define MyAppVersion "1.9.5"
#endif

#ifndef MySelfContained
  #define MySelfContained "0"
#endif

#ifndef MyBuildPath
  #define MyBuildPath MyScriptDir + "Publish\CortexFX_v" + MyAppVersion + "\app"
#endif

#ifndef MyOutputDir
  #define MyOutputDir MyScriptDir + "Publish"
#endif

; Fail early with a clear message instead of "No files found matching ..."
#if !FileExists(AddBackslash(MyBuildPath) + MyAppExeName)
  #error Publish output not found. Expected CortexFX.exe under Publish\CortexFX_v{#MyAppVersion}\app\. Run from the project root: powershell -ExecutionPolicy Bypass -File .\build.ps1
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
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes
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
Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
InternalCompressLevel=ultra64
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
; App binaries + core Resources (ffmpeg, ffmpeg_libs, Magick.NET, Pdfium) from the publish folder
Source: "{#MyBuildPath}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.cfxproj"; ValueType: string; ValueName: ""; ValueData: "CortexFX.VideoProject"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\CortexFX.VideoProject"; ValueType: string; ValueName: ""; ValueData: "Cortex FX Video Project"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\CortexFX.VideoProject\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKA; Subkey: "Software\Classes\CortexFX.VideoProject\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

#if MySelfContained == "0"
[Code]
const
  DotNet10DownloadUrl = 'https://dotnet.microsoft.com/en-us/download/dotnet/10.0';

function HasDotNet10Folder(const BaseDir: string): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if BaseDir = '' then
    Exit;
  if FindFirst(AddBackslash(BaseDir) + 'shared\Microsoft.WindowsDesktop.App\10.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function IsDotNet10DesktopInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;

  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('10.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;

  if HasDotNet10Folder(ExpandConstant('{commonpf64}\dotnet')) then
  begin
    Result := True;
    Exit;
  end;

  if HasDotNet10Folder(GetEnv('DOTNET_ROOT')) then
    Result := True;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
  Answer: Integer;
begin
  Result := True;
  if WizardSilent() or IsDotNet10DesktopInstalled() then
    Exit;

  Answer := MsgBox(
    'Cortex FX requires the free Microsoft .NET 10 Desktop Runtime (x64) to run.' + #13#10 +
    'It was not detected on this computer.' + #13#10#13#10 +
    'Would you like to open the official Microsoft download page now?' + #13#10#13#10 +
    'Yes - Open Microsoft .NET 10 download page and continue installing Cortex FX.' + #13#10 +
    'No - Continue installing Cortex FX without opening the browser.' + #13#10 +
    'Cancel - Exit Setup.',
    mbConfirmation, MB_YESNOCANCEL);

  if Answer = IDCANCEL then
  begin
    Result := False;
    Exit;
  end;

  if Answer = IDYES then
    ShellExec('open', DotNet10DownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;
#endif
