# Command / Query 目标架构与实施方案

> 2026-11-25。本次重构的实施依据。先确定终态，再按依赖顺序迁移；保留既有业务规则、用户数据格式与平台兼容行为。本文中的完成情况在实施后更新，不能把设计视为已验收。

## 1. 目标与边界

删除通用 Service 层，采用 Command / Query、Handler、状态上下文、Workflow 和平台适配器。使用 Microsoft.Extensions.Hosting 与 Microsoft.Extensions.DependencyInjection 管理构造和生命周期。CommandBus 是进程内分发器，采用显式泛型注册，不反射扫描，不引入消息队列、事件溯源或第二套数据库。

本次包含三项目拆分、应用入口切换、窗口契约迁移、播放与歌词职责迁移、存储和设置边界及测试迁移。界面样式、匹配算法、播放阈值、数据库 schema、版本号及发布产物不变；不新增 seek 或播放器适配。

## 2. 项目与依赖

| 项目 | 目标框架与内容 | 允许依赖 |
| --- | --- | --- |
| Stanza.Core | net10.0；Models、Playback、Lyrics、Workflows、Messaging、ReadModels、Ports | BCL、现有歌词比较依赖；不引用 WPF、WinRT、SQLite、Host、DI |
| Stanza.Infrastructure | net10.0-windows10.0.22621.0；SMTC、歌词 Provider、SQLite、设置文件、自启、文件日志 | Core、WinRT、SQLite、现有歌词 SDK、Serilog |
| Stanza | 既有 WPF 应用；Composition、Presentation、Desktop | Core；仅 Composition 装配 Infrastructure |
| Stanza.Tests | 既有 xUnit；规则、工作流、真实存储与应用契约回归 | 上述项目，测试数据目录继续隔离 |

UI 只持有 ICommandBus 和应用 Query 契约；不访问播放上下文、歌词加载器、WinRT 会话或容器。Core 通过端口访问平台。封面以原始字节跨越边界，由展示层保留 GDI+ 容错解码、缩放与冻结。

## 3. 状态与职责

| 对象 | 唯一拥有的状态 / 职责 | 原实现迁移来源 |
| --- | --- | --- |
| PlaybackContext | 当前会话身份、曲目、revision、播放状态、能力、切歌 FSM、位置追踪与时长证据 | SmtcSessionService 的业务字段、MediaRequestIdentity |
| LyricsContext | 当前歌词不可变行、generation、每曲手选世代与显示归属 | LyricsService 的状态字段 |
| LyricsLoader | 自动/候选加载、内存缓存、持久化写入排序、缓存准入 | LyricsService 的 IO 流程 |
| LyricsWorkflow | 记忆候选、手选与恢复自动用例、有限恢复调度及状态文案 | LyricsCoordinator |
| PlaybackWorkflow | 接收播放观察、切歌确认、请求歌词/封面、迟到结果准入 | SmtcSessionService 的跨模块流程 |
| CoverWorkflow | 曲目/会话归属、重试、去重、开关与原始图片发布 | 封面重试与 PlaybackCoverPipeline 的非 UI 部分 |
| SmtcPlaybackGateway | 会话发现/事件、媒体/时间线读取、播放控制、封面字节读取 | SmtcSessionSelector、SMTC 调用与源适配器 |
| PlaybackRuntime | 独立采样循环、平台事件订阅和应用任务停止 | 窗口业务 ticker、初始化和释放 |
| NowPlayingProjection | 发布完整只读快照、变更通知 | 原七种 UI 事件及零散属性读取 |

Handler 执行用户操作，Context 拥有状态，Workflow 管理跨步骤流程；不把旧大 Service 原样更名成 Handler。纯规则保留实现与回归断言；通用标题/歌手规则从 QQ Provider 提取到 Core，解除候选排序对具体词源的反向依赖。

## 4. Command / Query 契约

Command 为携带参数的 record，通过 ICommandBus.SendAsync 分发到唯一 ICommandHandler。无状态 Handler 为 Transient，显式注册在 Composition；总线在 Composition 解析处理器，Core 不认识 IServiceProvider。命令按业务意图命名：上一首、下一首、播放暂停、选择播放源、切换媒体区、应用歌词候选、设置本曲偏移、恢复自动匹配、清除歌词数据。

Query 包含读取当前显示快照、捕获/校验 LyricsSelectionContext、查询候选与读取单曲偏移。搜索候选虽访问网络，但不改变当前曲目或歌词，仍是 Query。加载并应用歌词属于 Command。GetSnapshot 不触碰平台 API、不推进 FSM、不写缓存。

一次渲染读取同一份快照，包含会话连接、曲目身份、播放状态/能力、切歌状态、位置采样、歌词行、单曲偏移、状态文本和封面。行和逐字集合发布后不可修改；快照引用在完整构造后替换。UI 的悬停、逐字动画、窗口定位和布局保留在展示层。

## 5. 线程与生命周期

- 状态变更在 IStateDispatcher 指定的串行执行上下文内进行；WPF 使用 Dispatcher 实现端口。网络/磁盘等待不持有全局命令锁，不因一首歌词慢而阻塞切歌。
- SMTC 事件先进入状态调度上下文；播放采样由 Host 运行时每 100ms 触发，窗口 ticker 只读快照并渲染。软件渲染降频只影响 UI。
- 会话 epoch / 元数据序号控制平台回包；曲目 revision 控制选择上下文；歌词 generation 控制同曲请求接管。三者独立保留，A→B→A 也不能复用旧上下文。
- 显示资格与缓存资格分别判断。切走的自动结果允许缓存；同曲旧自动不能覆盖手选行/记忆/缓存；写入串行闸门与恢复自动的先清理后加载语义保留。
- Context、Workflow、平台网关、投影、存储为 Singleton；无状态 Handler 为 Transient。数据库连接按操作创建并释放。窗口用工厂创建，关闭取消所属请求，不把 IDisposable Transient 从根容器反复解析。无隐式 Scoped。
- WPF 启动先做旧数据迁移、异常兜底、单实例检查，再构造 Host。移除 StartupUri，由窗口工厂创建主窗口，订阅快照后启动运行时。Host 不接管 WPF 消息循环。
- 退出先取消应用任务、退订平台事件和停止采样，再等待已启动的工作流结束，最后释放 Host、桌面对象、Mutex 并 Flush 日志。退出不得在 Dispatcher 上同步等待需要回到 Dispatcher 的任务。
- 数据库失败仍整体回退内存缓存、选择记忆和偏移；STANZA_DATA_DIR 及旧数据迁移规则不变。设置文件持久化与 Windows 自启分离。

## 6. 三条关键时序

### 切歌

1. 平台通知或采样触发早期信号，PlaybackContext 进入怀疑/切换；位置回退仍需稳定确认新 key，不能把拖动直接当切歌。
2. 带会话 epoch/元数据序号读取媒体；返回后校验身份，确认曲目并推进 revision，取消旧恢复、作废旧歌词 generation。
3. 发布等待快照，在锁外并行启动歌词流程与封面流程；暂停时只保留曲目信息，恢复播放再请求歌词。
4. 回包分别按 revision/generation 或会话/封面世代准入；过期结果不得改显示。歌词完成仍保留旧时间线闸门，再发布完整快照。

### 手选

1. Query 捕获选择上下文；Command 入口验证仍属当前曲目。
2. LyricsContext 推进 generation、登记手选接管，取消恢复；异步加载候选。
3. 返回后核验取消、revision 和 generation；成功按序写候选缓存及选择记忆并发布。纯音乐只临时显示，不写选择记忆。
4. 失败/取消保留已有歌词；没有歌词且仍是该曲时恢复自动加载。旧自动完成不能回写同曲手选结果。

### 恢复自动匹配

1. 验证选择上下文，删除该曲选择记忆，取消恢复并推进 generation。
2. 自动缓存删除排在已有写入之后；已接受的清理不随窗口关闭而撤销。
3. 使用原有统一候选决策重新加载，返回后按身份应用；取消后当前无词则恢复调度。单曲偏移保持独立。

## 7. 实施顺序与完成条件

| 阶段 | 工作 | 完成条件 |
| --- | --- | --- |
| 0 | 文档、基线与约定更新 | 本文可实施；禁止 DI 的旧约定已替换；旧回归先运行 |
| 1 | Core / Infrastructure 项目、规则/端口/平台迁移 | Core 独立编译，不引用平台；算法和存储回归保持 |
| 2 | Context、Loader、Workflow 职责提取 | 单一状态所有者；原 Service/Coordinator 删除；三套身份与缓存顺序覆盖 |
| 3 | Host、DI、CommandBus、Handler、Query、窗口工厂 | 显式注册和作用域校验；窗口仅使用应用契约；生命周期回归通过 |
| 4 | 独立播放循环、完整快照与文档收尾 | 窗口不驱动业务；释放可等待；无临时桥接或旧 Services 目录；Release 构建和全部测试通过 |

迁移期间允许短暂桥接，每阶段清楚标识；最终不保留双写状态或新旧两套运行链。

## 8. 验证与交付

自动验证：Release solution 构建；全部既有用例随真实实现迁移；新增命令分发/取消、Query 无副作用、不可变快照、无窗口采样、运行时停止/迟到结果、DI 注册及数据库回退的回归。测试运行真实规则和存储，平台端口用可控制观察输入验证时序，不复制业务实现。

实机验收单独记录：QQ 切歌/快切、暂停恢复、首次接入中段、外部拖动、迟到封面、选择器失效、全屏/多屏/DPI、第二实例与正常退出。自动测试不能替代以上平台验收。本次不自动启动主应用、操控播放器或覆盖 dist。

## 9. 实施记录

- 阶段 0–4 的代码迁移已完成：Core / Infrastructure / WPF、显式 Host/DI、10 种命令及 Handler、Query 与快照、独立采样和应用任务停止。旧 Services 层及临时桥接已移除。
- 当前请求身份归 PlaybackContext / MediaRequestIdentity，歌词世代及不可变行归 LyricsContext；缓存串行写入仍归 LyricsLoader。公共比较规则已提至 LyricsMatching，QQ Provider 的既有比较入口转发到它。
- 2026-11-25：迁移前 272/272；迁移及测试审计修正后 Release solution 构建 0 警告、0 错误，测试 **292/292**（原有 272 项加 20 项架构回归），`git diff --check` 通过。
- 新回归使用真实 DI、WPF Dispatcher、隔离 SQLite 与可控播放器/词源端口；覆盖无窗口采样、查询无业务副作用、跨曲结果与采样隔离、快照不可修改、命令互不阻塞、退出取消/等待、迟到封面和存储降级。
- 测试审计修正：公共比较用例直接调用 LyricsMatching；注册回归检查每种命令恰有一个可解析处理器；快照事件只收集结果，在测试中断言；旧会话通知检查读取次数；补齐恢复自动匹配、清缓存、播放源选择、媒体区开关与拒收封面的真实总线链路。
- 在临时副本中分别移除旧会话通知校验、将会话切换及曲目切换的重置采样改为可渲染，三项破坏分别触发预期测试失败；真实工作区业务代码未注入这些破坏。
- 发布版本为 0.5.0，由 v0.5.0 标签触发既有 GitHub Release 工作流；云端打包与发布结果以该次工作流为准。
- 本地临时目录中的不带运行时 ZIP 已完成打包、压缩完整性、Core / Infrastructure / DI / SQLite 依赖、许可文件及 SHA256 检查；未运行该程序包，其余发布包由云端工作流构建。
- 未做本次实网及真实窗口/播放器验收；第 8 节 QQ、DPI、多屏、全屏、单实例与退出的实机清单仍待人工确认。未启动主应用、未改用户数据、未覆盖本地 dist。

## 10. 框架依据

- [Generic Host](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host)：负责资源与生命周期，WPF 显式衔接启动/退出。
- [DI 生命周期与释放约定](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection-guidelines)：避免根容器长期捕获短生命周期 disposable，按实际所有权释放。
