; Stanza 安装脚本（Inno Setup 6/7）；通过 scripts/Package.ps1 -Installer 调用。
; 版本与发布目录由脚本从主项目读取后传入，不在本文件重复维护。
#ifndef AppVersion
  #error 请通过 scripts/Package.ps1 -Installer 构建安装包
#endif
#ifndef PublishDir
  #error 缺少 PublishDir
#endif
#ifndef PackageOutputDir
  #error 缺少 PackageOutputDir
#endif
#ifndef PackageName
  #error 缺少 PackageName
#endif
#ifndef SelfContained
  #error 缺少 SelfContained
#endif
#ifndef DesktopRuntimeMajor
  #error 缺少 DesktopRuntimeMajor
#endif

[Setup]
AppId={{7C3A5E2B-4D91-4F6A-9C2E-8A1B5D6F7C90}
AppName=Stanza
AppVersion={#AppVersion}
VersionInfoVersion={#GetVersionNumbersString(PublishDir + "\Stanza.exe")}
AppPublisher=Stanza
DefaultDirName={autopf}\Stanza
DefaultGroupName=Stanza
UninstallDisplayIcon={app}\Stanza.exe
SetupIconFile=Stanza\Assets\app.ico
OutputDir={#PackageOutputDir}
OutputBaseFilename={#PackageName}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=lowest

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Stanza"; Filename: "{app}\Stanza.exe"
Name: "{autodesktop}\Stanza"; Filename: "{app}\Stanza.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Stanza.exe"; Description: "运行 Stanza"; Flags: nowait postinstall skipifsilent

#if SelfContained == "0"
[Code]
function HasDesktopRuntime: Boolean;
var
  Root: String;
  FindRec: TFindRec;
begin
  Result := False;
  // 与 x64 apphost 一致，优先使用外部指定的运行时目录。
  Root := GetEnv('DOTNET_ROOT_X64');
  if Root = '' then Root := GetEnv('DOTNET_ROOT');
  if Root = '' then
    RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root);
  if Root = '' then Root := ExpandConstant('{commonpf64}\dotnet');
  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\{#DesktopRuntimeMajor}.*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Pos('-', FindRec.Name) = 0) then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup: Boolean;
var
  ResultCode: Integer;
begin
  Result := HasDesktopRuntime;
  if (not Result) and (not WizardSilent) then
    if MsgBox('此安装包不含运行时，需要 .NET {#DesktopRuntimeMajor} Desktop Runtime（Windows x64）。' + #13#10 +
      '如果使用 DOTNET_ROOT，请确认该目录中已安装对应运行时。是否打开官方下载页面？',
      mbInformation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/{#DesktopRuntimeMajor}.0', '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;
#endif
