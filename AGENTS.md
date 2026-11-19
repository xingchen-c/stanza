# AGENTS.md

面向 AI 编码代理（Codex / Cursor / Claude Code 等）的项目指南。动手前先读本文件，再按需翻阅末尾「文档索引」。

## 1. 项目是什么

**Stanza**（前身 FluentLyrics）——常驻 Windows 11 任务栏的歌词小组件（GPL-3.0）。应用版本只在 `Stanza/Stanza.csproj` 的 `<Version>` 维护，发布约定见根 README。
监听系统媒体会话（SMTC），实时显示当前播放器歌词：卡拉 OK 逐字揭晓、非中文歌中文翻译、长歌词跟随滚动、任务栏定位。当前优先稳定 QQ 音乐主流程；网易云音乐已有独立播放源策略，但新增适配与完整实机验收暂缓；其他播放器走 Generic SMTC 策略。这不影响网易云歌词源参与回退。

- **定位**：任务栏上「歌词优先」的轻量叠加卡片；长期演进为「任务栏音乐中心」（见 `doc/ROADMAP.md` 的 A/B 形态）。
- **唯一目标平台**：Windows 11（Windows 10 未实测）。
- **明确不做**：亚克力/重动效、运行期动态插件、DI 框架、大规模分层重构（见 ROADMAP「不做清单」）。

## 2. 技术栈与工程

- .NET 10 LTS，`TargetFramework: net10.0-windows10.0.22621.0`；`global.json` 限定稳定版 10.0 SDK，允许同版本内的功能带与补丁升级
- WPF（主界面）+ WinForms（仅托盘 `TrayIconService`）
- 主要第三方依赖：`Lyricify.Lyrics.Helper`（歌词搜索/解析/解密）、`Microsoft.Data.Sqlite`（缓存与选择记忆）、`Serilog` / 文件与异步 sink（日志）
- 测试：xUnit 2.9 + Microsoft.NET.Test.Sdk
- 打包：Inno Setup（`Stanza.iss`）

## 3. 构建 / 测试 / 发布

所有命令在**仓库根（含 `Stanza.slnx` 的那层）**执行：

```bash
# 构建（需 .NET 10 SDK，先确认 dotnet --version）
dotnet build Stanza.slnx -c Release

# 测试（测试程序集会在初始化前将数据目录隔离到临时目录）
dotnet test Stanza.slnx -c Release --no-build

# 发布（框架依赖版；本机更新固定在此目录，不另建修复目录）
dotnet publish Stanza/Stanza.csproj -c Release --self-contained false -o ../dist/release

# 打包（自包含 portable ZIP + SHA256，需 PowerShell 7；自动读取项目版本）
./scripts/Package.ps1

# 额外生成安装包（需 Inno Setup 6，iscc 在 PATH）
./scripts/Package.ps1 -Installer
```

最近验证（2026-11-19，.NET 10 LTS，SDK 10.0.404）：真实仓库 Release solution 构建 0 警告、0 错误；真实源码 **199/199** 测试通过，测试初始化器将数据隔离到独立临时目录，未替换日志实现。发布工作流的构建、测试和打包步骤已本地演练，临时目录 ZIP 完整性、自包含运行时、SQLite、许可及 SHA256 检查通过；云端运行与 Release 上传尚未触发，安装器最近一次于 2026-11-17 验证，现有 `dist` 未覆盖。旧日志清理、滚动与退出 Flush 未做专项验收。详细边界见 `doc/README.md`。

本机发布覆盖前先由用户退出 Stanza，不强杀、不自动启动 Debug 版；不要操控用户正在运行的播放器。自包含包、ZIP 和安装包不会随框架依赖发布自动更新，需单独授权与验证。`scripts/Package.ps1` 默认输出到 `../dist`，拒绝覆盖已有版本；验证时传临时 `-OutputDirectory`。发布标签必须为 `v<Version>`，可用 `-ReleaseTag` 检查名称；不自动打标签或推送。

`.github/workflows/release.yml` 在 GitHub 收到 `v*` 标签时，使用 Windows runner 校验版本、构建、测试、打包并发布自包含 ZIP 与 SHA256；需要标签提交已包含工作流及待发布源码。Gitee 附件同步、Scoop 清单更新及安装包不在当前自动流程中。推送标签会触发公开发布，仍需用户明确授权。

## 4. 代码结构速查

```
Stanza/                         # 仓库根
├── Stanza/                     # 主项目（WPF）
│   ├── App.xaml(.cs)           # WPF 入口 + 组合根启动 + 单实例/异常/迁移
│   ├── AppServices.cs          # 轻量组合根：数据库、歌词缓存、选择记忆
│   ├── AppDataPaths.cs         # 统一解析外部指定或默认的数据目录
│   ├── MainWindow.xaml(.cs)    # 窗口本体 + 事件中枢 + 显隐/穿透/定位（仍待继续拆分）
│   ├── MainWindow.Rendering.cs # 卡拉OK渲染、逐字、跟随滚动、翻译
│   ├── SettingsWindow / LyricsPickerWindow   # 设置窗口 / 候选选择窗口
│   ├── SettingsViewModel.cs    # 设置草稿、校验、AppSettings 构造
│   ├── Interop/NativeMethods.cs # 全项目 Win32/GDI/DWM P/Invoke 唯一入口
│   ├── Models/                 # LyricLine、LyricCandidate、LyricsQuery、AppSettings、NowPlaying、TrackKey…
│   └── Services/
│       ├── SmtcSessionSelector.cs          # 管理器、会话枚举、来源筛选和最终选择（不转发媒体事件）
│       ├── SmtcSessionService.cs           # 选中会话事件、播放状态、切歌、位置、歌词/封面编排
│       ├── Desktop/                        # ForegroundFullscreenDetector / WindowInteropService
│       ├── Storage/LocalDatabase.cs        # 实例化 SQLite 数据库
│       ├── Playback/                       # 播放源策略、位置追踪、封面、能力
│       │   ├── PlaybackCoverPipeline.cs
│       │   ├── PlaybackPositionTracker.cs
│       │   ├── TrackSwitchStateMachine.cs
│       │   ├── PlaybackSourceSelector.cs / Registry.cs
│       │   └── Policies/                   # QqMusic / NetEase / Generic
│       ├── Lyrics/
│       │   ├── ILyricsProvider / LyricsQuery
│       │   ├── LyricsProviderChain.cs       # 串行提供器回退与超时预算
│       │   ├── LyricsMemoryCache.cs         # 最近歌词内存缓存
│       │   ├── LyricsCoordinator.cs         # 加载、候选应用、NotFound 恢复
│       │   ├── LyricsService.cs             # 当前歌词门面与按位置取行
│       │   ├── SqliteLyricsCacheStore.cs / SqliteLyricsChoiceStore.cs
│       │   └── QqMusic / Netease / Lrclib Providers
│       ├── Taskbar/            # TaskbarService + Enumerator + LayoutCalculator + Locators
│       ├── TrayIconService.cs  # 托盘图标 + 右键菜单
│       ├── AppSettingsService.cs           # 设置 JSON 读写 + 开机自启（注册表）
│       └── LogService.cs       # AppLog 日志
├── Stanza.Tests/               # xUnit 测试（纯函数行为锁定）
├── doc/                        # 设计文档（见末尾索引）
├── Stanza.iss                  # Inno Setup 安装脚本
└── README.md / LICENSE / THIRD-PARTY-NOTICES.md / .gitignore
```

## 5. 核心数据流（主链路）

```
App.OnStartup
  → AppServices（LocalDatabase + SQLite repositories）
  → StartupUri 创建 MainWindow
    → MainWindow 构造（订阅事件、起 100ms ticker、起全屏检测定时器）
    → OnLoaded → SmtcSessionService.InitializeAsync
      → SmtcSessionSelector：GetSessions → PlaybackSourceSelector → SelectionChanged
      → SmtcSessionService 直接订阅选中会话事件 → UpdateSession → RefreshTrackAsync
        → LyricsCoordinator.LoadAsync → LyricsProviderChain（QQ → 网易云 → LRCLIB，先查内存/SQLite）
      → TrackLyricsChanged 事件 → MainWindow 渲染
    → OnTickerTick（每 100ms）
      → SamplePlayback（策略化快照外推）→ GetLineIndexAt（二分取当前行）→ UpdateLyricProgress（卡拉OK裁剪）
```

歌词默认源顺序 = **QQ → 网易云 → LRCLIB**，显式装配在 `SmtcSessionService` 创建 `LyricsCoordinator` 的构造位置。

## 6. 关键架构约定（必须遵守）

1. **无 DI 框架，但有组合根**：数据库和存储实例由 `AppServices` 创建；服务通过构造函数手动注入。**不要引入 `Microsoft.Extensions.DependencyInjection`**。
2. **词源编译期扩展**：新词源 = 实现 `ILyricsProvider` + 注册进 `LyricsCoordinator` 的构造参数列表。顺序即优先级。不做运行期动态加载 dll。
3. **播放源编译期扩展**：新播放器 = 实现 `IPlaybackSourcePolicy` + 提供对应 `IPlaybackSourceAdapter` + 注册进 `PlaybackSourceRegistry`。播放器来源与歌词来源是两条独立链路，禁止把两者耦合。新播放器策略必须先有实机 AUMID / 时间线数据；QQ 和网易云当前已经确认。
4. **切歌状态机是最高危区域**：位置回退 vs 拖动进度条、暂停/恢复时序、快照外推，历史上反复回归。状态迁移已收进 `TrackSwitchStateMachine`，改动前必须先补/跑测试（`Stanza.Tests`）。
5. **命名已定 Stanza**：勿改回 FluentLyrics。改名涉及 exe/程序集名、注册表自启键、单实例 Mutex、`%APPDATA%\Stanza` 目录与旧目录自动迁移。
6. **注释语言：中文**。XML doc 注释与行注释都是中文，新增代码保持一致。
7. **日志用 `AppLog`**（不用 `Console`/`Debug`）：Release 默认 `Info` 级，DEBUG 全量。关键决策打 `Info`，诊断打 `Debug`。
8. **防御式编程**：歌词接口 / 系统 API 普遍 `try/catch` 静默降级，失败不能影响主流程（卡片常驻）。
9. **现代 C# 语法**：可用 primary constructor、集合表达式 `[]`、target-typed `new()`、`record`、可空引用类型。
10. **测试友好**：核心纯逻辑标 `internal static`，主项目 `InternalsVisibleTo("Stanza.Tests")`，用历史真实歌名/时长钉死行为。

## 7. 易踩的坑

1. **`.slnx` 构建失败** → 改用 `.csproj`（见第 3 节）。
2. **SMTC 的 `Position` 是快照，不是实时位置**：它是「`LastUpdatedTime` 时刻」的值，QQ 约每秒推一次。渲染位置通过 `SmtcSessionService.SamplePlayback()` 和 `PlaybackPositionTracker` 策略化合成。改时序逻辑前先读懂 `IPlaybackSourcePolicy` 的时间线策略和现有测试。
3. **切歌 vs 拖动进度条**：位置大幅回退可能是拖动，须先确认歌曲 key 才切歌（`SmtcSessionService.SuspectTrackSwitch` / `PollForTrackConfirmationAsync`）。别把「位置回退」直接当切歌。
4. **Win32 物理像素 vs WPF DIP**：任务栏矩形是物理像素，窗口定位是 DIP，需 `TransformFromDevice` 换算（`TaskbarService.ToDips`）。
5. **WPF Dispatcher**：SMTC 事件回调在后台线程，必须在 `SmtcSessionService` 中通过 `Dispatcher.BeginInvoke` 回到 UI 线程，再访问播放状态和 UI 事件。
6. **静态与实例服务边界**：`AppLog`、`AppSettingsService` 保持静态；歌词缓存、选择记忆和 `LocalDatabase` 是实例对象，由 `AppServices` 持有。不要再新增全局静态存储。
7. **数据目录统一由 `AppDataPaths` 解析**：启动前通过 `STANZA_DATA_DIR` 指定，未设置时默认 `%APPDATA%\Stanza`；内含 `settings.json` / `stanza.db` / `logs/`。Scoop 使用 `persist/stanza/data`。旧歌词 JSON 只在选定目录内迁移；仅默认目录模式保留 `%APPDATA%\FluentLyrics` 自动迁移。不要在业务服务中另写固定用户路径。
8. **歌词自动匹配尚未统一**：当前自动链路是提供器串行回退，同名同歌手多版本问题未解决。禁止用多个 `LoadAsync` 并行抢答替代候选排序；目标设计见 `doc/MATCH-SCORING.md` §11。
9. **请求身份各有职责**：媒体请求的会话 epoch / 序号、曲目 revision、歌词 generation 不能互相替代。选词贯穿使用 `LyricsSelectionContext`，手选成功后不能被同曲自动晚到覆盖；关键回归约束见 `doc/HANDOVER.md`。
10. **候选展示与搜索召回分开**：列表始终过滤异歌，勾选只放宽歌手；搜索阶段可宽召回，不能把任意括号副标题都当作可忽略版本。不要把歌手分档降级改回整源一票否决。

## 8. 文档索引

仓库根 README 是用户入口；`doc/` 下的 6 份开发文档纳入版本控制，和代码同步维护：

- `README.md` — 功能 / 安装 / 构建（仓库根的用户文档）
- `doc/README.md` — 开发文档总索引、当前状态、验证基线与公开范围
- `doc/HANDOVER.md` — 当前架构、职责、关键回归约束与接手顺序（改核心流程前必读）
- `doc/ROADMAP.md` — 方向、未完成项、优先级与**不做清单**（动架构前必读）
- `doc/SMTC-SOURCE-POLICY-IMPLEMENTATION-PLAN.md` — 当前播放源策略、QQ 时序参数、扩展边界与待办
- `doc/MATCH-SCORING.md` — 当前匹配、手动过滤、候选缓存与未实现的统一决策
- `doc/SOFTWARE-RENDER-FALLBACK.md` — 当前软件渲染降级与人工验收

本地历史盘点、原始需求、旧 SMTC 方案、长篇修复工单和对话备份不再作为必读入口，也不纳入后续提交。新克隆仓库不应依赖这些文件。新增公开文档须同时更新本索引、`doc/README.md` 和 `.gitignore` 白名单；忽略规则不会清除既有 Git 历史。
