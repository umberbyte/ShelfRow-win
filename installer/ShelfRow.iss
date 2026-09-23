#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\installer\publish"
#endif
#ifndef PrerequisiteDir
  #define PrerequisiteDir "..\artifacts\installer\prerequisites"
#endif
#ifndef OutputPath
  #define OutputPath "..\artifacts\installer\dist"
#endif
#ifndef Architecture
  #define Architecture "x64"
#endif
#ifndef RuntimeIdentifier
  #define RuntimeIdentifier "win-x64"
#endif
#ifndef InnoArchitecture
  #define InnoArchitecture "x64os"
#endif
#ifndef VCRuntimeArchitecture
  #define VCRuntimeArchitecture "x64"
#endif
#define VCRuntimeFile "vc_redist." + Architecture + ".exe"
#define VCRuntimeVersion GetVersionNumbersString(PrerequisiteDir + "\" + VCRuntimeFile)

[Setup]
AppId={{B8737020-7FF5-4DDC-B2A1-4E0D09A7B92D}
AppName=ShelfRow
AppVersion={#AppVersion}
AppPublisher=Go Sugawara
AppCopyright=Copyright (c) 2026 Go Sugawara
DefaultDirName={autopf}\ShelfRow
DefaultGroupName=ShelfRow
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=admin
ArchitecturesAllowed={#InnoArchitecture}
ArchitecturesInstallIn64BitMode={#InnoArchitecture}
MinVersion=10.0.19041
OutputDir={#OutputPath}
OutputBaseFilename=ShelfRow-{#AppVersion}-{#RuntimeIdentifier}-Setup
UninstallDisplayIcon={app}\ShelfRow.App.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
#ifdef SignToolName
SignTool={#SignToolName}
SignedUninstaller=yes
#endif

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PrerequisiteDir}\{#VCRuntimeFile}"; Flags: dontcopy
Source: "{#PrerequisiteDir}\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ShelfRow"; Filename: "{app}\ShelfRow.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\ShelfRow"; Filename: "{app}\ShelfRow.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ShelfRow.App.exe"; Description: "{cm:LaunchProgram,ShelfRow}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[CustomMessages]
japanese.PrerequisiteError=必要なランタイムのインストールに失敗しました: %1 (コード %2)。ネットワーク接続を確認して再実行してください。
english.PrerequisiteError=Could not install required runtime: %1 (code %2). Check your network connection and run Setup again.
japanese.RuntimeNotice=必要に応じて Visual C++ ランタイムと WebView2 をインストールします。WebView2 が未導入の場合はインターネット接続が必要です。
english.RuntimeNotice=Setup installs Visual C++ and WebView2 runtimes if needed. Internet access is required when WebView2 is missing.

[Code]
var
  PrerequisiteRestart: Boolean;

function HasWebView2(): Boolean;
var
  Version: String;
  Key: String;
begin
  Key := 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  Result := (RegQueryStringValue(HKLM32, Key, 'pv', Version) and
    (Version <> '') and (Version <> '0.0.0.0'));
  if not Result then
    Result := (RegQueryStringValue(HKCU32, Key, 'pv', Version) and
      (Version <> '') and (Version <> '0.0.0.0'));
end;

function HasVCRuntimeInView(RootKey: Integer): Boolean;
var
  Version: String;
  Installed: Cardinal;
  PackedVersion: Int64;
  RequiredVersion: Int64;
  Key: String;
begin
  Key := 'Software\Microsoft\VisualStudio\14.0\VC\Runtimes\{#VCRuntimeArchitecture}';
  Result := False;
  if not RegQueryDWordValue(RootKey, Key, 'Installed', Installed) or (Installed <> 1) then
    exit;
  if not RegQueryStringValue(RootKey, Key, 'Version', Version) then
    exit;
  if Copy(Version, 1, 1) = 'v' then Delete(Version, 1, 1);
  if StrToVersion(Version, PackedVersion) and StrToVersion('{#VCRuntimeVersion}', RequiredVersion) then
    Result := ComparePackedVersion(PackedVersion, RequiredVersion) >= 0;
end;

function HasVCRuntime(): Boolean;
begin
  Result := HasVCRuntimeInView(HKLM32) or HasVCRuntimeInView(HKLM64);
end;

function InstallRuntime(const FileName, Parameters: String): String;
var
  ExitCode: Integer;
begin
  Result := '';
  ExtractTemporaryFile(FileName);
  if not Exec(ExpandConstant('{tmp}\') + FileName, Parameters, '', SW_HIDE,
    ewWaitUntilTerminated, ExitCode) then
    Result := FmtMessage(CustomMessage('PrerequisiteError'), [FileName, IntToStr(ExitCode)])
  else if ExitCode = 3010 then
    PrerequisiteRestart := True
  else if ExitCode <> 0 then
    Result := FmtMessage(CustomMessage('PrerequisiteError'), [FileName, IntToStr(ExitCode)]);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not HasVCRuntime() then begin
    Result := InstallRuntime('{#VCRuntimeFile}', '/install /quiet /norestart');
    if Result <> '' then exit;
  end;
  if not HasWebView2() then begin
    Result := InstallRuntime('MicrosoftEdgeWebview2Setup.exe', '/silent /install');
    if Result <> '' then exit;
    if not HasWebView2() then
      Result := FmtMessage(CustomMessage('PrerequisiteError'), ['WebView2', 'not detected']);
  end;
end;

function NeedRestart(): Boolean;
begin
  Result := PrerequisiteRestart;
end;

procedure InitializeWizard();
begin
  WizardForm.WelcomeLabel2.Caption := WizardForm.WelcomeLabel2.Caption + #13#10#13#10 + CustomMessage('RuntimeNotice');
end;
