# Stanza

在 Windows 任务栏实时显示系统媒体播放器歌词的轻量小组件——透明、点击穿透、歌词优先。QQ 音乐桌面版已实机适配；网易云音乐已有独立 SMTC 策略和 AUMID 识别，完整时序仍在继续验收；其他标准 SMTC 播放器走通用策略。

通过系统媒体会话（SMTC）监听当前播放，逐行同步歌词，支持卡拉 OK 式双色进度（已播放/未播放逐字揭晓）、非中文歌中文翻译、长歌词跟随滚动。

> **Stanza**（诗的一节 / 歌词的一段）：名字取「歌词的最小结构单位」，也为后续从「歌词卡片」长成「任务栏音乐中心」留足空间。

仓库：<https://gitee.com/xing-chen-d/stanza>

## 功能

- **任务栏歌词卡片**：支持多显示器；定位可选「贴 FluentFlyout 组件」或「独立定位（不依赖外部组件）」
- **播放器接入**：自动选择 Windows SMTC 会话；QQ 音乐、网易云音乐有独立来源策略，未知播放器使用 Generic 策略
- **歌词获取**：QQ 音乐优先（逐字 QRC）、网易云音乐兜底（逐字 YRC）、LRCLIB 最后兜底，逐行同步
- **翻译**：非中文歌自动显示 QQ 提供的中文翻译（正词 + 翻译双行显示）
- **卡拉 OK 进度**：已播放部分亮色、未播放部分暗色，逐字跟随演唱节奏推进；长歌词跟随滚动
- **媒体控制**：鼠标悬停呼出「封面 + 歌名/歌手 + 播放控制」，支持手动选择歌词版本（记住选择）
- **体验细节**：切歌不残留旧歌词、拖动进度条不误触切歌、暂停隐藏可配置、全屏游戏/视频自动隐藏、歌词缓存（内存 + 磁盘）
- **设置**：字号 / 歌词颜色 / 歌词偏移 / 歌词栏宽度 / 任务栏定位（白名单 + 放置方向）/ 显示封面与播放控制 / 暂停时隐藏卡片 / 点击穿透 / 开机自启（JSON 持久化）
- **托盘**：显示 / 隐藏、设置、打开日志目录、清除歌词缓存、歌词选择、调整位置、退出；单实例保护

## 环境要求

- Windows 11（Windows 10 未实测）
- QQ 音乐桌面版：请在设置中开启「系统媒体传输控制（SMTC）」
- 网易云音乐桌面版：已接入专用播放源策略；由于各版本 SMTC 事件质量不同，暂停恢复和切歌仍在持续实机验收，seek 尚未支持
- 其他播放器：需发布标准 SMTC 会话；目前只保证通用链路可用，尚未逐一完成实机验收
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（框架依赖版需要；自包含包无需安装）

## 安装与使用

1. 从 [Gitee Releases](https://gitee.com/xing-chen-d/stanza/releases) 页下载最新的 zip 包并解压
2. 运行 `Stanza.exe`，歌词卡片出现在任务栏
3. 托盘右键菜单可「显示 / 隐藏」「设置」「退出」

> 首次使用建议先播放一首歌，确认播放器已发布 SMTC；暂停播放时卡片会自动隐藏。

## 从源码构建

需安装 .NET 10 SDK（LTS）。`global.json` 允许稳定版 10.0 的功能带与补丁升级，不会自动选用 .NET 11。在仓库根目录（含 `Stanza.slnx` 的那层）执行：

```powershell
# 框架依赖版（需 .NET 10 Desktop Runtime，体积小）
dotnet publish Stanza/Stanza.csproj -c Release --self-contained false -o ../dist/release

# 自包含 portable ZIP + SHA256（需 PowerShell 7；版本从主项目读取）
./scripts/Package.ps1

# 额外生成安装包（需 Inno Setup 6，iscc 在 PATH）
./scripts/Package.ps1 -Installer
```

上面两个打包命令二选一，`-Installer` 会同时生成 ZIP 和安装包。

打包脚本默认输出到仓库外的 `../dist`，不会更新 `dist/release`；可用 `-OutputDirectory` 指定其他输出目录。portable 是程序免安装版，配置仍保存在 `%APPDATA%\Stanza`，不是数据随身版。相同版本的输出已存在时脚本会停止，不覆盖旧包。

## 版本与发布约定

- 使用 [语义化版本](https://semver.org/lang/zh-CN/)：`主版本.次版本.修订号`。在 `0.x` 阶段，小修复或随包运行时更新递增修订号，功能新增或较大改进递增次版本号；使用与升级约定稳定后进入 `1.0.0`。
- **唯一版本来源是 `Stanza/Stanza.csproj` 的 `<Version>`**。只修改这一处；不要修改第三方依赖自身的版本号来同步应用版本。预发布使用 `-alpha.N`、`-beta.N` 或 `-rc.N`。
- 设置页展示产品版本（保留预发布后缀）；日志、崩溃报告保留完整信息版本和 SDK 自动附加的 Git 提交号。Windows 数字文件版本不包含预发布后缀。
- 打包脚本读取项目版本，并将同一版本用于 portable ZIP、SHA256、安装包和安装器元信息；`Stanza.iss` 不再手填版本。
- 正式发布前先提交并通过构建、回归及包验收，再创建 `v<Version>` 标签；GitHub/Gitee 的 Release 使用同一标签及同一份附件，不把平台自动生成的源码 ZIP 当成程序包。
- 已公开的版本内容不可替换；即使只修复打包遗漏或更新内置运行时，也递增版本后重新发布。Git 构建信息不代替可排序的版本号。
- Release 和未来的 Scoop 清单应与项目版本、下载文件名、SHA256 一致。当前没有自动创建 Release 或更新 Scoop 清单的流程；预发布不进入默认稳定版清单。

以 `0.3.0` 为例，对应标签 `v0.3.0`、`Stanza-0.3.0-win-x64-portable.zip`、`Stanza-0.3.0-setup.exe`。发布或 CI 打包时传入标签做一致性检查：

```powershell
./scripts/Package.ps1 -ReleaseTag v0.3.0
```

`-ReleaseTag` 只校验传入的标签名称，不创建、推送或验证远程标签。脚本也不会创建 Release、提交或推送代码。

## 配置

设置保存在 `%APPDATA%\Stanza\settings.json`，删除该文件即可恢复默认设置。

歌词缓存和手动选择记忆保存在同一个 SQLite 数据库 `%APPDATA%\Stanza\stanza.db`。旧版 `lyrics-cache.json` / `lyrics-choice.json` 会在首次启动时自动迁移并改名为 `.migrated`。

## 常见问题

**任务栏没有出现歌词卡片？**

确认播放器正在播放，并且 QQ 音乐已开启「系统媒体传输控制（SMTC）」。网易云音乐应发布标准 SMTC 会话；其他播放器也需要自行支持并发布标准 SMTC 会话。

**显示「未找到歌词」或「纯音乐」？**

自动匹配未找到可用歌词；可通过托盘「歌词选择…」手动挑选其他版本（选中后会自动记住）。

**需要额外安装什么？**

自包含 portable ZIP（`*-portable.zip`）完整解压后免安装运行；框架依赖包需要安装 .NET 10 Desktop Runtime。

## 许可证

GPL-3.0，见 [LICENSE](LICENSE)。第三方依赖许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

视觉与实现思路参考 FluentFlyout / BetterLyrics（GPL-3.0）与 TaskbarLyrics（MIT），仅参考思路，不包含其代码。
