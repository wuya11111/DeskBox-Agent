; DeskBox MCP 扩展独立安装脚本。
; MCP 与 DeskBox 本体使用不同的 AppId，因此可以单独安装、升级和卸载。

#ifndef MyAppVersion
#define MyAppVersion "1.4.8-agent.1"
#endif
#ifndef McpReleaseDir
#define McpReleaseDir "..\artifacts\mcp-publish"
#endif

#define MyAppName "DeskBox MCP 扩展"
#define MyAppPublisher "DeskBox Agent 修改版维护者"
#define MyAppExeName "DeskBox.Mcp.exe"

[Setup]
AppId={{14C11CB4-1B47-4451-9EAA-4431AF0A45E7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=为 DeskBox 增加本地 AI 控制接口；不包含 DeskBox 本体。
UninstallDisplayName={#MyAppName} {#MyAppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19044
DefaultDirName={code:GetDeskBoxInstallDir}
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
UsePreviousAppDir=no
UsePreviousPrivileges=yes
CloseApplications=force
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
UninstallFilesDir={app}\mcp\.installer
OutputDir=..\Output
OutputBaseFilename=DeskBox_MCP_Setup
SetupIconFile=..\src\DeskBox\Assets\deskbox.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Files]
Source: "{#McpReleaseDir}\DeskBox.Mcp.exe"; DestDir: "{app}\mcp"; Flags: ignoreversion
Source: "{#McpReleaseDir}\README.md"; DestDir: "{app}\mcp"; DestName: "MCP配置说明.md"; Flags: ignoreversion
Source: "{#McpReleaseDir}\LICENSE"; DestDir: "{app}\mcp"; Flags: ignoreversion
Source: "{#McpReleaseDir}\LICENSE_CHANGE.md"; DestDir: "{app}\mcp"; Flags: ignoreversion

[Run]
Filename: "{app}\mcp\MCP配置说明.md"; Description: "打开 MCP 中文配置说明"; Flags: shellexec postinstall skipifsilent unchecked

[UninstallDelete]
Type: dirifempty; Name: "{app}\mcp"

[Code]
const
  DeskBoxInstallStateKey = 'Software\DeskBox\DirectInstall';

function IsDeskBoxDirectory(Path: string): Boolean;
begin
  Result := (Path <> '') and FileExists(AddBackslash(Path) + 'DeskBox.exe');
end;

function TryRegisteredDeskBoxDirectory(RootKey: Integer; var Path: string): Boolean;
begin
  Path := '';
  Result :=
    RegQueryStringValue(RootKey, DeskBoxInstallStateKey, 'InstallLocation', Path) and
    IsDeskBoxDirectory(Path);
end;

function FindDeskBoxDirectory: string;
begin
  Result := '';
  if TryRegisteredDeskBoxDirectory(HKEY_CURRENT_USER, Result) then Exit;
  if TryRegisteredDeskBoxDirectory(HKEY_LOCAL_MACHINE, Result) then Exit;
  if IsDeskBoxDirectory(ExpandConstant('{localappdata}\Programs\DeskBox')) then
    Result := ExpandConstant('{localappdata}\Programs\DeskBox')
  else if IsDeskBoxDirectory(ExpandConstant('{localappdata}\DeskBox')) then
    Result := ExpandConstant('{localappdata}\DeskBox')
  else if IsDeskBoxDirectory(ExpandConstant('{commonpf}\DeskBox')) then
    Result := ExpandConstant('{commonpf}\DeskBox');
end;

function GetDeskBoxInstallDir(Param: string): string;
begin
  Result := FindDeskBoxDirectory;
  if Result = '' then
    Result := ExpandConstant('{autopf}\DeskBox');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and
     (not IsDeskBoxDirectory(WizardDirValue)) then
  begin
    MsgBox(
      '所选目录中没有 DeskBox.exe。请先安装 DeskBox 本体，或选择 DeskBox.exe 所在目录。',
      mbError,
      MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
begin
  Result := '';
  if not IsDeskBoxDirectory(ExpandConstant('{app}')) then
    Result := '未找到 DeskBox 本体。请先安装 DeskBox 本体，再安装 MCP 扩展。';
end;
