# Stanza 当前架构与维护交接

> 最近核对：2026-11-20。本文描述现行职责与维护约束，不保存某次机器路径、分支、HEAD 或脏工作区快照。验证结果统一见 [文档索引](./README.md#最近验证基线)。

## 1. 接手入口

Stanza 是 Windows 11 任务栏上的轻量歌词小组件，基于 SMTC 获取播放上下文，显示同步歌词、逐字卡拉 OK、翻译、封面和播放控制。

- .NET 10 LTS / WPF；WinForms 只用于托盘。目标框架为 `net10.0-windows10.0.22621.0`；`global.json` 限定稳定版 10.0 SDK，不跨主版本滚动。
- 当前优先维护 QQ 音乐主流程；网易云播放策略保留，新增适配暂缓。
- 播放器来源与歌词来源相互独立；QQ 播放器也可以使用网易云或 LRCLIB 的歌词。
- 无 DI 框架、无动态插件；优先小步原地演进，不重写整个仓库。

接手前先看根目录 `AGENTS.md`、本文件和对应专题文档，再检查 `git status --short` 与 diff。不能假设提交已经包含用户工作区中的改动，也不能回退不属于自己的修改。

## 2. 目录与职责

路径均相对仓库根：

| 位置 | 当前职责 |
| --- | --- |
| `Stanza/App.xaml.cs` | 启动、旧数据目录迁移、单实例、异常兜底 |
| `Stanza/AppServices.cs` | 创建数据库、缓存和选择记忆；数据库不可用时回退内存实现 |
| `Stanza/Models/` | `NowPlaying`、`LyricsQuery`、`LyricsSelectionContext`、歌词行、候选与设置 |
| `Stanza/Services/SmtcSessionSelector.cs` | SMTC 管理器、会话枚举、来源筛选和选择；不转发媒体事件 |
| `Stanza/Services/SmtcSessionService.cs` | 直接订阅选中会话；编排播放状态、曲目、歌词、封面与控制 |
| `Stanza/Services/Playback/` | 来源策略/适配器、位置追踪、切歌 FSM、能力与封面管线 |
| `Stanza/Services/Lyrics/` | 提供器、候选聚合、缓存门面、手选与 NotFound 恢复 |
| `Stanza/Services/Storage/` | 实例化 SQLite 数据库 |
| `Stanza/Services/Desktop/` | 全屏检测、窗口样式与光标互操作 |
| `Stanza/Services/Taskbar/` | 任务栏枚举、布局、锚点与独立定位 |
| `Stanza/Interop/NativeMethods.cs` | Win32 / GDI / DWM P/Invoke 入口 |
| `Stanza/MainWindow.xaml.cs` | 窗口事件中枢、显隐、定位、交互及渲染驱动，仍较重 |
| `Stanza/MainWindow.Rendering.cs` | 逐字裁剪、翻译、长歌词跟随滚动 |
| `Stanza/LyricsPickerWindow.xaml(.cs)` | 异步候选选择、取消/重搜、曲目上下文失效与应用反馈 |
| `Stanza/SettingsWindow.xaml(.cs)` / `SettingsViewModel.cs` | 设置界面、草稿、校验与保存 |
| `Stanza.Tests/` | 匹配、播放纯逻辑、请求身份、歌词并发、设置与存储测试 |

解决方案当前只有主项目和测试项目，尚未抽出 `Stanza.Core`。未来结构不应被描述成已经存在。

## 3. 启动与运行链路

```text
App.OnStartup → AppServices → MainWindow
  → SmtcSessionService.InitializeAsync
  → SmtcSessionSelector 枚举 / 筛选 / 选择
  → SmtcSessionService 订阅选中会话的媒体 / 时间线 / 播放状态事件
  → 来源策略规范化元数据 → NowPlaying / LyricsQuery
  → LyricsCoordinator → 选择记忆 / 缓存 / LyricsProviderChain
  → UI 事件 → MainWindow.Rendering

Dispatcher ticker → SamplePlayback → GetLineIndexAt → 卡拉 OK 进度
```

- SMTC 回调需通过 Dispatcher 回到 UI 线程，再访问播放状态与 UI 事件。
- ticker 正常为 `100ms`，降级模式为 `250ms`；现已常驻驱动交互采样，没有会话时不做播放采样，避免按钮穿透状态无法恢复。
- `MainWindow` 不再直接读取原始 SMTC 会话，位置统一由 `SamplePlayback()` 提供。

## 4. 请求身份与歌词一致性

三种身份各有职责，不可互相替代：

1. `MediaRequestIdentity` 的会话 epoch / 元数据序号：拦截旧会话或旧媒体请求回包。
2. 曲目 `Revision`：固定选择窗口打开时的歌曲身份；A → B → A 后，原 A 窗口也不能继续应用。
3. `LyricsService.Generation`：作废在飞歌词请求，防迟到结果污染显示、状态和手选结果。

`LyricsSelectionContext(LyricsQuery Query, long Revision)` 贯穿候选搜索与应用。搜索开始、返回和应用阶段均需检查上下文，不能在应用时临时读取当前曲目并把旧候选绑定过去。

手选先取消恢复并开启新 generation。同曲自动请求即使晚到，也不能覆盖手选歌词或缓存；失败或取消且当前无歌词时，使用最近的同曲查询恢复自动加载，仍受曲目 key 和 generation 约束。正常切歌后的旧自动请求仍可缓存自己的曲目，但不能回写当前显示。缓存异步写入顺序亦有约束，不能只保护 `_lines` 而漏掉持久化副作用。

选择窗口立即显示加载态、单实例打开；关闭取消等待，失败可重试，成功才关闭。窗口已设 `Topmost` 和主窗口 `Owner`。

## 5. 必须保留的回归约束

这些约束提炼自历史修复，不依赖本地长篇工单：

| 区域 | 不变量 | 优先查看的测试 |
| --- | --- | --- |
| 匹配 | 歌手优先分档；没有匹配歌手时允许降级，不能整源放弃 | `QqMusicMatchingTests`、`QqCandidateSelectionTests` |
| 手选过滤 | 异歌始终过滤；勾选只放宽同名版本的歌手；搜索清洗不等于同曲判定 | `MediaInteractionTests`、`QqCandidateSelectionTests` |
| 请求身份 | 旧会话、旧元数据回包不能改变当前曲目；不能只靠歌词 generation | `MediaRequestTests` |
| 歌词并发 | 手选优先；失败或取消后可恢复同曲自动加载；迟到请求不得修改新歌显示、状态或选择记忆 | `LyricsGenerationTests`、`LyricsSelectionTests` |
| 缓存 | 记忆候选先查专用缓存；不能把旧普通缓存当成该候选的歌词 | `LyricsSelectionTests`、`SqliteStorageTests` |
| 纯音乐 | 解析失败不等于纯音乐；误选纯音乐不持久记忆，避免阻断后续匹配 | `QqMusicMatchingTests`、`LyricsSelectionTests` |
| 位置 | SMTC Position 是 LastUpdatedTime 时刻的快照；位置回退不等于切歌 | `PlaybackSourceTests` |
| 恢复边界 | 恢复不应清掉仍有效的切歌闸门；新歌未确认时不能错误重置墙钟位置 | `PlaybackSourceTests` |
| 切歌计时 | TickCount 的负数、零值与回绕都必须正常计算耗时及最近切歌窗口 | `TrackSwitchStateMachineTests` |
| NotFound | `Ready` / `Retry` / `Abort` 分开；暂停可等待，取消不回写，终止须清理加载占位 | `LyricsRecoveryTests` |
| 超时 | 单源超时应继续后续源；候选查询有总预算，保留已取得结果 | `LyricsProviderChainTests` |
| 设置 | 保存、预览和取消不能静默重置日志级别或丢失其他字段 | `SettingsViewModelTests` |
| 全屏 | 覆盖整块目标显示器才隐藏；系统外壳不算全屏，另一显示器全屏不影响当前卡片 | `FullscreenDetectionTests` |
| 任务栏 | 优先沿用当前任务栏；原任务栏移除后按主任务栏和稳定顺序重新选择 | `TaskbarSelectionTests` |

改行为时先添加能失败的回归用例；不能为了让重构通过而反转原本锁定正确行为的断言。第二实例退出与真实 WinRT 事件退订等入口行为还需人工检查，不能从纯逻辑测试推导其已验收。

## 6. UI、封面与平台边界

- 点击穿透由 `WindowInteropService` 按调整模式、穿透设置和光标是否位于媒体区域，动态切换 `WS_EX_TRANSPARENT`，保留 `WS_EX_NOACTIVATE`；不能用仅同线程转发的 `HTTRANSPARENT` 替代跨进程穿透。
- 播放能力优先读取运行时 `PlaybackInfo.Controls`，读取失败才回退到策略能力。当前尚未实现主动 seek。
- 任务栏矩形使用物理像素，定位 WPF 窗口时才换为 DIP，避免重复缩放。
- 无锚点时沿用已定位的任务栏，避免枚举顺序变化导致跨屏跳位；原任务栏消失后才重新选择。保存的歌词字号与颜色在启动和设置应用时共用同一入口。
- 全屏检测用选中任务栏定位显示器，再比较前台窗口的 DWM 可见边界；读取失败回退 `GetWindowRect`。开始菜单、搜索和 Shell 宿主被排除，Explorer 文件窗口 F11 全屏保留。
- 显示入口统一遵守手动隐藏、全屏与播放状态；退出全屏时，调整位置模式也应恢复可见，不重新移动用户正在拖动的位置。真实窗口显隐仍需人工验收。
- 封面适配器决定读取和替换策略，`PlaybackCoverPipeline` 负责过期结果、去重、解码与发布。QQ / Generic 接受首张稳定图，网易云允许有限窗口内替换迟到图。
- 会话选择按实际 SMTC 对象去重，不能用 AUMID 与枚举下标判断对象是否替换；封面重读在异步返回后再次核对重试世代、会话和释放状态。
- 封面保留 GDI+ 容错解码，转 WPF 图像后冻结；不要轻易退回直接 `BitmapImage` 读取，历史上遇过 JPEG / ICC 失败。
- 软件渲染三项降级见 [兼容文档](./SOFTWARE-RENDER-FALLBACK.md)，不要改成移除透明分层窗口。

## 7. 存储与日志

用户数据由 `AppDataPaths` 统一定位：启动前设置 `STANZA_DATA_DIR`，未设置时默认 `%APPDATA%\Stanza`；内含 `settings.json`、`stanza.db`、`logs/`。Scoop 清单通过 `persist` 和 `env_set` 指向 `persist/stanza/data`。旧 JSON 只在选定目录内迁移；外部指定目录不会隐式导入 AppData，默认模式保留旧 FluentLyrics 目录迁移。

- `LocalDatabase`、缓存和选择记忆是实例对象，由 `AppServices` 持有；不要新增全局静态存储。
- `AppLog` 与 `AppSettingsService` 保持静态。日志用 `AppLog`，关键决策 Info、诊断 Debug。
- SQLite 共用 `lyrics_cache` / `lyrics_choice`，WAL；`synchronous=NORMAL` 和 `busy_timeout=3000` 需在连接层正确应用。
- 手选的候选专用缓存与普通曲目缓存不同，详见 [匹配文档](./MATCH-SCORING.md)。
- 真实 `AppLog` 初始化会创建目录并清理旧日志。测试程序集现通过模块初始化器，在业务静态服务初始化前将 `STANZA_DATA_DIR` 指向独立临时目录，并清除单文件覆盖变量；测试运行真实存储和日志实现。新增独立探针仍需显式规划数据目录，不能依赖测试程序集替它隔离。

## 8. 接手与验证顺序

1. 检查工作区 diff，不覆盖用户修改，不自动提交/推送。
2. 依据改动选择现有测试；一般构建/测试命令见根 `AGENTS.md`，最近结果和隔离边界见 [索引](./README.md#最近验证基线)。
3. 修改播放时序时，除纯逻辑测试外，安排 QQ 切歌、暂停恢复、外部拖动、快速连切和首次接入中段播放的人工验收。
4. 修改交互时，人工确认首击、按钮能力、选择器置顶和曲目变化；修改平台行为时补 DPI、多屏和全屏验收。
5. 不自动启动 Debug 版，不关闭或操控用户正在运行的 Stanza / 播放器。需要真实界面或实网验证时先明确授权与范围。
6. 本机更新发布固定在 `../dist/release`，不另建修复目录，不强杀进程。

当前使用 .NET 10 LTS，不能套用 .NET 11 才引入的 `Environment.TickCount` 睡眠计时变化。休眠恢复仍需人工验收；数据目录配置不改播放计时逻辑。

待办与优先级只在 [ROADMAP.md](./ROADMAP.md) 维护；跨源候选决策仍是计划，不是本轮已完成功能。
