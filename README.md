# Stanza

在 Windows 任务栏实时显示系统媒体播放器歌词的轻量小组件——透明、点击穿透、歌词优先。QQ 音乐桌面版已实机适配，其他标准 SMTC 播放器走通用策略。

通过系统媒体会话（SMTC）监听当前播放，逐行同步歌词，支持卡拉 OK 式双色进度（已播放/未播放逐字揭晓）、非中文歌中文翻译、长歌词跟随滚动。

> **Stanza**（诗的一节 / 歌词的一段）：名字取「歌词的最小结构单位」，也为后续从「歌词卡片」长成「任务栏音乐中心」留足空间。

仓库：<https://gitee.com/xing-chen-d/stanza>

## 功能

- **任务栏歌词卡片**：支持多显示器；定位可选「贴 FluentFlyout 组件」或「独立定位（不依赖外部组件）」
- **播放器接入**：自动选择 Windows SMTC 会话；QQ 音乐有独立来源策略，未知播放器使用 Generic 策略
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
- 其他播放器：需发布标准 SMTC 会话；目前只保证通用链路可用，尚未逐一完成实机验收
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（使用 Release 包时需要）

## 安装与使用

1. 从 [Gitee Releases](https://gitee.com/xing-chen-d/stanza/releases) 页下载最新的 zip 包并解压
2. 运行 `Stanza.exe`，歌词卡片出现在任务栏
3. 托盘右键菜单可「显示 / 隐藏」「设置」「退出」

> 首次使用建议先播放一首歌，确认播放器已发布 SMTC；暂停播放时卡片会自动隐藏。

## 从源码构建

在仓库根目录（含 `Stanza.slnx` 的那层）执行：

```bash
# 框架依赖版（需 .NET 10 Desktop Runtime，体积小）
dotnet publish Stanza\Stanza.csproj -c Release -o ..\dist\release

# 自包含单文件版（免安装，体积约 100MB）
dotnet publish Stanza\Stanza.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ..\dist\Stanza-0.2.0-win-x64
```

## 配置

设置保存在 `%APPDATA%\Stanza\settings.json`，删除该文件即可恢复默认设置。

## 常见问题

**任务栏没有出现歌词卡片？**

确认播放器正在播放，并且 QQ 音乐已开启「系统媒体传输控制（SMTC）」。其他播放器需要自行支持并发布标准 SMTC 会话。

**显示「未找到歌词」或「纯音乐」？**

自动匹配未找到可用歌词；可通过托盘「歌词选择…」手动挑选其他版本（选中后会自动记住）。

**需要额外安装什么？**

自包含包（`*-selfcontained`）免安装直接运行；框架依赖包需要安装 .NET 10 Desktop Runtime。

## 许可证

GPL-3.0，见 [LICENSE](LICENSE)。第三方依赖许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

视觉与实现思路参考 FluentFlyout / BetterLyrics（GPL-3.0）与 TaskbarLyrics（MIT），仅参考思路，不包含其代码。
