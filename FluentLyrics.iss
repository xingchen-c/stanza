; FluentLyrics 安装脚本（Inno Setup 6）
; 前置：先 publish 自包含包（见下方命令），再编译本脚本：
;   iscc FluentLyrics.iss
; 输出：..\dist\FluentLyrics-0.2.0-setup.exe

[Setup]
AppId={{7C3A5E2B-4D91-4F6A-9C2E-8A1B5D6F7C90}
AppName=FluentLyrics
AppVersion=0.2.0
AppPublisher=FluentLyrics
DefaultDirName={autopf}\FluentLyrics
DefaultGroupName=FluentLyrics
UninstallDisplayIcon={app}\FluentLyrics.exe
OutputDir=..\dist
OutputBaseFilename=FluentLyrics-0.2.0-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
; 自包含发布目录（dotnet publish FluentLyrics\FluentLyrics.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist\FluentLyrics-0.2.0-win-x64）
Source: "..\dist\FluentLyrics-0.2.0-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\FluentLyrics"; Filename: "{app}\FluentLyrics.exe"
Name: "{autodesktop}\FluentLyrics"; Filename: "{app}\FluentLyrics.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FluentLyrics.exe"; Description: "运行 FluentLyrics"; Flags: nowait postinstall skipifsilent
