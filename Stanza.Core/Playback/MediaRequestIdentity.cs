namespace Stanza.Core.Playback;

/// <summary>
/// 分别用会话 epoch、元数据请求序号和曲目 revision 判断回包归属，三者不能相互替代。
/// </summary>
internal sealed class MediaRequestIdentity
    {
        private object? _session;
        private long _epoch;
        private long _metadataSequence;
        private LyricsQuery? _query;
        private bool _metadataReady;

        /// <summary>会话或曲目变化即递增；即使 A→B→A，旧 A 的选择上下文也已失效。</summary>
        public long Revision { get; private set; }

        public void ChangeSession(object? session)
        {
            _session = session;
            _epoch++;
            _metadataSequence++;
            Revision++;
            _query = null;
            _metadataReady = false;
        }

        /// <summary>开启新的媒体读取，使同会话更早发出的元数据请求失效。</summary>
        public MetadataRequest BeginMetadata(object session)
            => new(session, _epoch, ++_metadataSequence);

        /// <summary>只捕获身份，供恢复探测使用，不抢占正在进行的元数据读取。</summary>
        public MetadataRequest Capture(object session) => new(session, _epoch, _metadataSequence);

        public bool IsSessionCurrent(MetadataRequest request)
            => ReferenceEquals(_session, request.Session) && request.Epoch == _epoch;

        public bool IsCurrent(MetadataRequest request)
            => IsSessionCurrent(request) && request.Sequence == _metadataSequence;

        public bool ConfirmTrack(LyricsQuery query)
        {
            // 补全时长或专辑仍是同一曲目，不应使已经打开的选择窗口失效。
            var changed = _query?.Key != query.Key;
            if (changed)
                Revision++;
            _query = query;
            _metadataReady = !string.IsNullOrWhiteSpace(query.Title);
            return changed;
        }

        public void SetMetadataUnavailable() => _metadataReady = false;

        public LyricsSelectionContext? CreateContext()
            => _session is not null && _metadataReady && _query is not null
                ? new LyricsSelectionContext(_query, Revision)
                : null;

        public bool IsTrackCurrent(LyricsSelectionContext context)
            => _session is not null && context.Revision == Revision && context.Key == _query?.Key;

        /// <summary>短暂缺失元数据时仍可保留曲目身份，但不接受新的用户选择。</summary>
        public bool IsSelectionCurrent(LyricsSelectionContext context)
            => _metadataReady && IsTrackCurrent(context);

        internal readonly record struct MetadataRequest(object Session, long Epoch, long Sequence);
    }
