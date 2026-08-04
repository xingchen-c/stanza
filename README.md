# FluentLyrics

在 Windows 任务栏实时显示 QQ 音乐歌词的 Fluent 风格小组件。通过系统媒体会话（SMTC）监听当前播放，逐行同步歌词，支持卡拉 OK 式已播放/未播放双色进度和非中文歌的中文翻译。

## 功能

- **任务栏歌词卡片**：自动定位在 FluentFlyout 音乐组件右侧、开始按钮左侧；支持多显示器，组件在哪条任务栏就贴哪条
- **歌词获取**：QQ 音乐搜索（QRC / LRC 多格式解析），LRCLIB 兜底，逐行同步
- **翻译**：非中文歌自动显示 QQ 提供的中文翻译（正词 + 翻译双行显示）
- **卡拉 OK 进度**：已播放部分亮色、未播放部分暗色，随播放进度平滑推进
- **体验细节**：暂停自动隐藏、切歌不残留旧歌词、拖动进度条不误触切歌、歌词内存缓存（切回听过的歌秒出）
- **设置**：字号 / 颜色 / 滚动速度 / 歌词偏移 / 点击穿透 / 开机自启（JSON 持久化）
- **托盘**：显示 / 隐藏、设置、退出；单实例保护

## 环境要求

- Windows 11（Windows 10 未实测）
- QQ 音乐桌面版：请在设置中开启「系统媒体传输控制（SMTC）」
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（使用 Release 包时需要）

## 安装与使用

1. 从仓库 Releases 页下载最新的 zip 包并解压
2. 运行 `FluentLyrics.exe`，歌词卡片出现在任务栏
3. 托盘右键菜单可「显示 / 隐藏」「设置」「退出」

> 首次使用建议先播放一首歌，确认 QQ 音乐已开启 SMTC；暂停播放时卡片会自动隐藏。

## 从源码构建

```bash
dotnet publish FluentLyrics.csproj -c Release
```

## 配置

设置保存在 `%APPDATA%\FluentLyrics\settings.json`，删除该文件即可恢复默认设置。

## 常见问题

**任务栏没有出现歌词卡片？**

确认 QQ 音乐设置里已开启「系统媒体传输控制（SMTC）」并正在播放歌曲（暂停时卡片会自动隐藏）。

**显示「纯音乐」？**

说明该歌曲在歌词源中未匹配到可用的同步歌词（QQ 音乐与 LRCLIB 均无），并非程序错误。

**需要额外安装什么？**

Release 包为框架依赖版，需要安装 .NET 10 Desktop Runtime。

## 许可证

GPL-3.0，见 [LICENSE](LICENSE)。第三方依赖许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

视觉与实现思路参考 FluentFlyout / BetterLyrics（GPL-3.0）与 TaskbarLyrics（MIT），仅参考思路，不包含其代码。
