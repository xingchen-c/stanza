; Stanza 安装脚本（Inno Setup 6）
; 前置：先 publish 自包含包（见下方命令），再编译本脚本：
;   iscc Stanza.iss
; 输出：..\dist\Stanza-0.2.0-setup.exe

[Setup]
AppId={{7C3A5E2B-4D91-4F6A-9C2E-8A1B5D6F7C90}
AppName=Stanza
AppVersion=0.2.0
AppPublisher=Stanza
DefaultDirName={autopf}\Stanza
DefaultGroupName=Stanza
UninstallDisplayIcon={app}\Stanza.exe
OutputDir=..\dist
OutputBaseFilename=Stanza-0.2.0-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
; 自包含发布目录（在解决方案根目录执行）：
;   dotnet publish Stanza\Stanza.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ..\dist\Stanza-0.2.0-win-x64
Source: "..\dist\Stanza-0.2.0-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Stanza"; Filename: "{app}\Stanza.exe"
Name: "{autodesktop}\Stanza"; Filename: "{app}\Stanza.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Stanza.exe"; Description: "运行 Stanza"; Flags: nowait postinstall skipifsilent
