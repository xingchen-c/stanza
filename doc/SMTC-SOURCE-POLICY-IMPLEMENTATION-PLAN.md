# SMTC 播放源策略与实施边界

> 最近核对：2026-11-25。主体已实现；本文只保留现行职责、行为约束和明确待办，不再作为逐步重复执行的迁移工单。当前优先稳定 QQ，网易云播放器新增适配暂缓。

## 1. 两条独立链路

```text
播放器 SMTC → 播放源策略 / 适配器 → NowPlaying / LyricsQuery
                                             ↓
                       歌词源 QQ / 网易云 / LRCLIB → 统一候选决策
                                             ↓
                                   歌词与渲染投影
```

`IPlaybackSourcePolicy` 描述播放器行为，`ILyricsProvider` 搜索和解析歌词；两者独立。切换播放器不应强制切换歌词源，也不应复制一份完整的播放工作流。

## 2. 现行职责

路径相对仓库根，规则与策略位于 `Stanza.Core/Playback/`，平台实现位于 `Stanza.Infrastructure/Playback/`：

| 类型 / 位置 | 职责 |
| --- | --- |
| `Stanza.Core/Models/NowPlaying.cs` | 播放上下文与 UI 投影契约 |
| `Stanza.Core/Models/LyricsQuery.cs` | 歌词查询输入，不参与来源选择 |
| `SmtcPlaybackGateway` / `SmtcPlaybackSession` | 管理器初始化、枚举、来源选择、WinRT 事件转换与播放控制，向 Core 提供纯端口 |
| `PlaybackSourceSelector` | 纯逻辑选择与排序，可独立测试 |
| `PlaybackSourceRegistry` | 编译期绑定策略与适配器 |
| `IPlaybackSourcePolicy` / `Policies/` | 来源识别、元数据规范化、切歌与时间线参数、默认能力 |
| `PlaybackStateMapper` | 平台端口状态映射到项目状态 |
| `PlaybackPositionTracker` | 原始快照与边界信息合成为显示位置 |
| `TrackSwitchStateMachine` | Idle / Suspected / Switching / Loading 与最近完成窗口 |
| `IPlaybackSourceAdapter` / `PlaybackCoverAdapter` | 来源侧封面读取、节流与替换策略 |
| `CoverWorkflow` / `CoverDecoder` | 前者管理归属、重试及字节去重；后者在展示层解码与冻结 |
| `PlaybackContext` / `PlaybackWorkflow` / `PlaybackRuntime` | 状态唯一归属、业务编排、独立采样与生命周期 |
| `Stanza/MainWindow.xaml.cs` | 通过 CommandBus 操作、Query 读取快照；ticker 只渲染 |

Core 已独立为 net10.0 项目，IPlaybackGateway / IPlaybackSession 隔离 WinRT，具体契约与实现记录见 [架构文档](./ARCHITECTURE.md)。

## 3. 生命周期与请求身份

1. 选择器初始化 SMTC 管理器，结合系统当前会话与枚举结果选择来源；定时刷新以发现会话变化。
2. 选择变化后，工作流退订旧会话，切换策略/适配器，订阅新会话的 `MediaPropertiesChanged`、`TimelinePropertiesChanged`、`PlaybackInfoChanged`。
3. 回调先通过 Dispatcher 回到 UI 线程；旧 sender 不得触发当前会话刷新。
4. 读取媒体属性前捕获请求身份，await 后校验会话 epoch 与元数据序号，再确认曲目。
5. 曲目变化推进 revision，歌词请求另用 generation；断开/Dispose 不能让旧回包重新建立当前状态。

`MediaRequestIdentity` 的三个维度不可混用：

- epoch 标识会话生命周期；
- metadata sequence 判断媒体回包新旧；
- revision 标识选词上下文对应的曲目生命周期。

歌词 generation 只保护歌词请求，挡不住旧媒体回包主动重置曲目。因此“已有 generation”不能成为移除媒体身份检查的理由。

## 4. 当前来源策略

| 来源 | Key / AUMID | 当前行为与限制 |
| --- | --- | --- |
| QQ 音乐 | `QQ` / `QQMusic.exe` | 信任可用 Position / LastUpdatedTime，支持新鲜快照外推和早期切歌信号；主要维护对象 |
| 网易云音乐 | `NetEase` / `cloudmusic.exe` | 历史观察时间线退化，使用边界与墙钟合成；保留实现，未完成全矩阵验收 |
| Generic | `Generic` / 未识别来源 | 公共保守策略，不代表每种播放器都已适配 |

网易云历史实机观察：`TimelinePropertiesChanged` 为 0 次、Position 恒 `0ms`、LastUpdatedTime 为 1601 年。这是当时样本，不保证所有未来客户端版本都如此；不要据此复制或猜测新的阈值。

### QQ 参数基线

| 分组 | 已有参数 |
| --- | --- |
| 疑似切歌 | 位置回退阈值 `2000ms`；确认间隔 `200ms`、连续确认 2 次、最多 10 次 |
| 元数据轮询 | 间隔 `200ms`、最多 20 次 |
| 切歌恢复 | 看门狗 `8000ms`、误判恢复 `3000ms`、最近完成忽略窗口 `2000ms` |
| 早期信号 | Stopped、Changing、时长变化、位置回退均启用；时长变化阈值 `500ms` |
| 旧快照与毛刺 | 旧快照窗口 `3000ms`；位置毛刺窗口 `2000ms`、阈值 `10000ms` |
| 时间线 | 新鲜快照最大年龄 `2000ms`，允许快照外推与墙钟兜底；暂停 seek 阈值 `2000ms` |

参数集中在策略类型。修改前先跑位置与切歌测试，再用实机数据验证，不在 MainWindow 或其他路径重复加入同一套阈值。

## 5. 位置与切歌不变量

- SMTC Position 是 LastUpdatedTime 时刻的快照，不是调用时的实时位置。
- 位置大幅回退可能是用户拖动；先进入 Suspected 并确认曲目 key，不得直接清歌词或跳回开头。
- 新歌 key 未确认时，不能用错误的墙钟零点替代仍有效的旧位置。
- 首次接入已经播放到中段的 QQ 歌曲，不能一律从 0 开始。
- 暂停时冻结显示位置，避免微小回退导致卡拉 OK 来回跳；明确的大幅外部 seek 仍需按策略处理。
- 恢复边界不能清除有效的切歌闸门，旧快照不能把新歌拖回上一首位置。
- 暂停期间确认新曲目时清理旧词，恢复后可以加载，不能因已有旧歌词而永久跳过刷新。
- 时间线能用于位置计算，不意味着 EndTime 已证明属于当前新歌；匹配时长已由 `TrackDurationEvidence` 限定归属；后续换曲等待元数据确认后的时间线，未知时长传空，见[匹配文档](./MATCH-SCORING.md#2-时长归属与播放来源)。

## 6. 歌词与选择器接入

Query 提供选择上下文与候选搜索，应用候选通过命令：

```csharp
LyricsSelectionContext? CreateLyricsSelectionContext();
bool IsLyricsSelectionCurrent(LyricsSelectionContext context);
Task<IReadOnlyList<LyricCandidate>> GetLyricCandidatesAsync(
    LyricsSelectionContext context, CancellationToken cancellationToken = default);
// 应用操作由 ICommandBus 分发给 ApplyLyricsCandidateHandler。
await commands.SendAsync(new ApplyLyricsCandidate(context, candidate), cancellationToken);
```

窗口必须贯穿使用同一上下文，不能取完 A 的候选后以当前 B 的标题生成应用 key。即使 A → B → A，旧窗口也因 revision 不同而失效。

歌词加载、手选和 NotFound 恢复由 `LyricsWorkflow` 处理，不在播放器策略里复制。手选取消恢复并取得新的歌词 generation；当前候选过滤、缓存与超时见 [MATCH-SCORING.md](./MATCH-SCORING.md)。

## 7. 封面与控制能力

- 适配器控制是否读取、是否允许同曲替换；工作流检查归属并去重、发布字节，展示层解码。
- QQ / Generic 使用首张稳定封面；网易云允许 5 秒窗口内的迟到封面替换。
- CoverWorkflow 按 `150 / 350 / 800 / 1500ms` 尝试重读迟到封面。
- 保留 GDI+ 对 JPEG / ICC 的容错，再转换并冻结 WPF 图像。避免为简化代码回退为直接解码。
- UI 控制能力优先来自 `PlaybackInfo.Controls`，读取失败才用策略默认值；播放暂停需兼容 toggle 能力。
- 当前有上一首 / 播放暂停 / 下一首，主动 seek 未实现，策略 `SupportsSeek = false`。

## 8. UI 与线程边界

- `PlaybackRuntime` 独立采样，`MainWindow` 的 ticker 读取同一份快照，再驱动歌词二分定位与逐字裁剪。
- ticker 已常驻采样窗口交互；无会话时不做播放采样，不应因断开会话停止恢复穿透状态所需的采样。
- 原生窗口样式由 `WindowInterop` 处理；物理像素到 DIP 的转换放在定位边界。
- 不为了支持一个来源，把 WinRT 原始会话重新暴露给渲染层。

## 9. 扩展一个播放源

1. 先收集目标客户端的 AUMID、状态序列、Position / LastUpdatedTime / EndTime 与事件样本。
2. 实现 `IPlaybackSourcePolicy`，只描述差异；需要不同封面行为时提供对应适配器。
3. 注册进 `PlaybackSourceRegistry`，保留 Generic 的兜底边界。
4. 用纯逻辑测试锁定来源识别、选择、状态映射、时序参数和位置行为。
5. 明确实机验收矩阵，通过后再宣称适配完成。

禁止引入 DI 框架、运行期 DLL 插件、复制完整服务或混入歌词源选择。当前暂停网易云新增适配，并不删除已存在的策略。

## 10. 未完成项与验收

未完成：播放来源设置 UI、词源配置、主动 seek、协调器进一步减负，以及统一候选决策的实网校准。优先级统一见 [ROADMAP.md](./ROADMAP.md)。

自动回归重点：`PlaybackSourceTests`、`MediaRequestTests`、`LyricsGenerationTests`、`LyricsRecoveryTests`、`LyricsSelectionTests` 和 `MediaInteractionTests`。最近验证结果与日志隔离限制见 [索引](./README.md#最近验证基线)。

QQ 人工验收至少覆盖：

- 启动时已有播放、播放中切歌、快速连续切歌、暂停后切歌再恢复；
- 进度前后拖动与恢复后旧快照，确保不把 seek 当切歌；
- 选词期间切歌、关闭后重搜、手选后自动晚到；
- 初始空封面与补图，按钮能力与跨进程穿透；
- QQ 退出重启、多会话切换与会话身份复用，不能仅凭人工构造的 descriptor 测试宣称已通过；
- 多屏/DPI、全屏与休眠恢复。

不自动启动 Debug 版或操控用户播放器。网易云全矩阵仍未完成，恢复该方向时单独安排，不把保留策略等同于已验收。
