using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Windows.Media.Control;
using FluentLyrics.Models;
using FluentLyrics.Services;

namespace FluentLyrics;

/// <summary>歌词渲染：卡拉OK逐字、跟随式滚动、翻译与布局（MainWindow 分部）</summary>
public partial class MainWindow
{
    /// <summary>渲染当前歌词行：暗色全文垫底，亮色层用 Clip 按进度从左揭开</summary>
    private void ShowLyricLine(LyricLine line, int positionMs)
    {
        LyricTextDim.Text = line.Text;
        LyricTextDim.Foreground = _lyricDimBrush;
        LyricTextBright.Text = line.Text;
        LyricTextBright.Foreground = _lyricBrush;

        // 非中文歌词且有翻译 → 双行显示；中文歌/无翻译保持单行
        var translation = ShouldShowTranslation(line) ? line.Translation : null;
        LyricTranslation.Text = translation ?? string.Empty;
        LyricTranslation.Visibility = string.IsNullOrEmpty(translation)
            ? Visibility.Collapsed
            : Visibility.Visible;

        _lastProgress = -1;
        LyricTextBright.Clip = null; // 清掉上一行的裁剪，避免行时长缺失时残留旧边界
        _lastClipWidth = -1;
        ReapplyLyricDisplay(); // 先完成布局，UpdateLyricProgress 需要文本实际宽度
        UpdateLyricProgress(positionMs);
    }

    /// <summary>
    ///     是否显示翻译：数据驱动——QQ 提供了译文且译文与正词不同才显示。
    ///     不猜歌词语言：中文歌 QQ 一般不返回译文（或返回相同文本），会自动跳过；任何语言通用。
    /// </summary>
    private static bool ShouldShowTranslation(LyricLine line)
    {
        if (string.IsNullOrWhiteSpace(line.Translation))
            return false;

        // QQ 翻译里的 "//" 占位（无翻译），不显示
        if (string.Equals(line.Translation.Trim(), "//", StringComparison.Ordinal))
            return false;

        return !string.Equals(line.Translation.Trim(), line.Text.Trim(), StringComparison.Ordinal);
    }

    /// <summary>按连续进度右移亮色层的 Clip 右缘（0~1），只在明显变化时更新</summary>
    private void UpdateLyricProgress(int positionMs)
    {
        if (_currentIndex < 0 || _currentIndex >= _smtc.Lyrics.Lines.Count)
            return;

        var line = _smtc.Lyrics.Lines[_currentIndex];
        var width = LyricTextBright.ActualWidth;
        if (width <= 0)
            return;

        double clipWidth;
        if (line.HasWordTiming && TryGetWordClipWidth(line, positionMs, out var wordClip))
        {
            clipWidth = wordClip; // 逐字：按演唱节奏点亮
        }
        else
        {
            var duration = GetLineDuration(_currentIndex);
            if (duration <= 0)
                return;

            var progress = Math.Clamp((positionMs - line.StartTime) / (double)duration, 0, 1);
            if (Math.Abs(progress - _lastProgress) < 0.002)
                return;
            _lastProgress = progress;
            clipWidth = width * progress; // 兜底：整行线性揭晓
        }

        if (Math.Abs(clipWidth - _lastClipWidth) < 0.5)
            return;
        _lastClipWidth = clipWidth;

        // 亮色层只露出 0..clipWidth 部分：一条干净竖直边界扫过整行，不受字形段拆分影响
        LyricTextBright.Clip = new RectangleGeometry(
            new Rect(0, 0, clipWidth, LyricTextBright.ActualHeight));

        // 长歌词跟随演唱进度滚动：亮色边界（正在唱的位置）始终保持在视口内
        UpdateLyricScroll(clipWidth);
    }

    /// <summary>
    ///     跟随式滚动：长歌词不再来回跑马灯，而是让亮色边界保持在视口内——
    ///     唱过的字从左侧滚出、未唱的字从右侧滚入；唱到行尾停止，换行时 ReapplyLyricDisplay 归零。
    /// </summary>
    private void UpdateLyricScroll(double clipWidth)
    {
        var viewportWidth = LyricViewport.Width;
        var textWidth = LyricStack.ActualWidth;
        if (viewportWidth <= 0 || textWidth <= viewportWidth)
        {
            // 短歌词：静态显示在视口左缘
            LyricTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            LyricTranslate.X = 0;
            return;
        }

        // 亮色边界锚定在视口宽度 65% 处：边界越过该位置后歌词开始左移
        const double anchor = 0.65;
        var targetX = Math.Min(0, anchor * viewportWidth - clipWidth);
        // 不滚过头：行尾保留右侧空白
        targetX = Math.Max(targetX, viewportWidth - textWidth - MarqueeEndPadding);

        if (Math.Abs(targetX - LyricTranslate.X) < 0.5)
            return;

        // 150ms 平滑跟随，避免随逐字进度跳变
        var animation = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(animation, 30);
        LyricTranslate.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    /// <summary>
    ///     逐字揭晓：找到当前演唱到的字（绝对时间二分），亮色边界 = 之前各字累计宽度 + 当前字内平滑进度。
    ///     逐字文本与正词对不上时返回 false，由调用方回退线性揭晓。
    /// </summary>
    private bool TryGetWordClipWidth(LyricLine line, int positionMs, out double clipWidth)
    {
        clipWidth = 0;
        var words = line.Words;
        var boundaries = GetWordBoundaries(line);
        if (words is null || boundaries is null || boundaries.Length != words.Count)
            return false;

        int lo = 0, hi = words.Count - 1, index = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (words[mid].StartTime <= positionMs)
            {
                index = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (index < 0)
            return true; // 还没唱到第一个字：不亮

        var word = words[index];
        var before = index > 0 ? boundaries[index - 1] : 0;
        var wordWidth = boundaries[index] - before;
        var fraction = word.EndTime > word.StartTime
            ? Math.Clamp((positionMs - word.StartTime) / (double)(word.EndTime - word.StartTime), 0, 1)
            : 1;
        clipWidth = before + fraction * wordWidth;
        return true;
    }

    /// <summary>
    ///     计算一行里每个逐字片段结束位置对应的像素宽度（缓存按行文本复用）。
    ///     逐字片段需能按顺序在正词里匹配到，否则返回 null 表示该行不适合逐字渲染。
    /// </summary>
    private double[]? GetWordBoundaries(LyricLine line)
    {
        var words = line.Words;
        if (words is null || words.Count == 0)
            return null;

        if (_wordBoundaryCache.TryGetValue(line.Text, out var cached))
            return cached;

        var endChar = new int[words.Count];
        var charPos = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var text = words[i].Text;
            var found = line.Text.IndexOf(text, charPos, StringComparison.Ordinal);
            if (found < 0)
                return null; // 逐字文本与正词对不上，该行回退线性揭晓
            charPos = found + text.Length;
            endChar[i] = charPos;
        }

        var boundaries = new double[words.Count];
        var typeface = new Typeface(
            LyricTextBright.FontFamily,
            LyricTextBright.FontStyle,
            LyricTextBright.FontWeight,
            LyricTextBright.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i < words.Count; i++)
        {
            var prefix = line.Text.Substring(0, endChar[i]);
            var formatted = new FormattedText(
                prefix,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                LyricTextBright.FontSize,
                Brushes.Black,
                dpi);
            boundaries[i] = formatted.WidthIncludingTrailingWhitespace;
        }

        _wordBoundaryCache[line.Text] = boundaries;
        return boundaries;
    }

    /// <summary>行的持续时间：优先取 EndTime，缺省时用下一行开始时间兜底</summary>
    private int GetLineDuration(int index)
    {
        var lines = _smtc.Lyrics.Lines;
        if (index < 0 || index >= lines.Count)
            return 0;

        var line = lines[index];
        if (line.EndTime is { } end)
            return end - line.StartTime;

        return index + 1 < lines.Count
            ? lines[index + 1].StartTime - line.StartTime
            : 0;
    }

    /// <summary>
    ///     计算当前播放位置（毫秒）。
    ///     SMTC 的 Position 是"上次更新时刻"的快照，QQ 音乐约每秒才推送一次，
    ///     直接使用会导致歌词切换滞后；这里用 LastUpdatedTime 外推到当前时刻。
    /// </summary>
    private int GetPositionMs(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline,
        GlobalSystemMediaTransportControlsSession session)
    {
        var position = timeline.Position;

        // 仅在播放中做外推；暂停/停止时直接用上报的位置
        if (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
            if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(2)) position += elapsed;
        }

        // 全局歌词偏移：正数 = 歌词提前显示
        position += TimeSpan.FromMilliseconds(_settings.LyricOffsetMs);

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        return (int)position.TotalMilliseconds;
    }

    private void SetStatus(string text)
    {
        Dispatcher.Invoke(() => UpdateLyricDisplay(text));
    }

    /// <summary>显示歌词/状态文本：短文本贴左（紧挨组件卡片），超长文本跑马灯滚动</summary>
    private void UpdateLyricDisplay(string text)
    {
        // 状态文本只走亮色层（无进度裁剪），暗色层清空避免残留上一行歌词的"鬼影"
        LyricTextDim.Text = string.Empty;
        LyricTextBright.Text = text;
        LyricTextBright.Foreground = _lyricBrush;
        LyricTextBright.Clip = null;
        _lastClipWidth = -1;
        LyricTranslation.Text = string.Empty;
        LyricTranslation.Visibility = Visibility.Collapsed;
        ReapplyLyricDisplay();
    }

    private void ReapplyLyricDisplay()
    {
        if (LyricHost.ActualWidth <= 0)
            return;

        LyricStack.UpdateLayout();
        var hostWidth = LyricHost.ActualWidth;
        var hostHeight = LyricHost.ActualHeight;
        var textWidth = LyricStack.ActualWidth;

        // 媒体模式下左侧封面、右侧按钮占用宽度，歌词可用宽度相应收缩
        var coverWidth = CoverBorder.Visibility == Visibility.Visible
            ? CoverBorder.ActualWidth + 6 /* 左边距 */ + 8 /* 与歌词间距 */
            : 0;
        var controlsWidth = MediaControlsPanel.Visibility == Visibility.Visible
            ? MediaControlsPanel.ActualWidth + 4 /* 右边距 */
            : 0;
        _textBaseLeft = coverWidth;
        var availableWidth = Math.Max(0, hostWidth - _textBaseLeft - controlsWidth);

        // 歌词视口：限定在封面与按钮之间，超出部分裁剪（避免歌词滚到控制按钮下面）
        LyricViewport.Width = availableWidth;
        LyricViewport.Height = hostHeight;
        Canvas.SetLeft(LyricViewport, _textBaseLeft);
        Canvas.SetTop(LyricViewport, 0);

        // 诊断：尺寸或文本变化才打印，避免刷屏；帮助确认跑马灯分支是否进入
        var diagSig = $"{hostWidth:F0}|{textWidth:F0}|{availableWidth:F0}|{LyricTextBright.Text}";
        if (diagSig != _reapplyDiagSig)
        {
            _reapplyDiagSig = diagSig;
            AppLog.Debug($"歌词布局: host={hostWidth:F0} text={textWidth:F0} avail={availableWidth:F0} 文本='{LyricTextBright.Text}'");
        }

        // 垂直居中（相对视口）
        Canvas.SetTop(LyricStack, Math.Max(0, (LyricViewport.Height - LyricStack.ActualHeight) / 2));

        // 短文本：视口内贴左（紧挨封面）静态显示
        if (textWidth <= availableWidth - 8)
        {
            LyricTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            Canvas.SetLeft(LyricStack, 0);
            LyricTranslate.X = 0;
            return;
        }

        // 长文本：不再来回跑马灯，重置到开头，由 UpdateLyricProgress 按卡拉 OK 进度跟随滚动
        LyricTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        Canvas.SetLeft(LyricStack, 0);
        LyricTranslate.X = 0;
    }
}
