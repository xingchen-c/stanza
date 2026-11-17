; Stanza 安装脚本（Inno Setup 6）；通过 scripts/Package.ps1 -Installer 调用。
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

[Setup]
AppId={{7C3A5E2B-4D91-4F6A-9C2E-8A1B5D6F7C90}
AppName=Stanza
AppVersion={#AppVersion}
VersionInfoVersion={#GetVersionNumbersString(PublishDir + "\Stanza.exe")}
AppPublisher=Stanza
DefaultDirName={autopf}\Stanza
DefaultGroupName=Stanza
UninstallDisplayIcon={app}\Stanza.exe
OutputDir={#PackageOutputDir}
OutputBaseFilename=Stanza-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
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
