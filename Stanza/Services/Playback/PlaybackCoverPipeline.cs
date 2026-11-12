using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Stanza.Interop;

namespace Stanza.Services.Playback;

/// <summary>
///     封面公共管线：负责读取结果的生命周期检查、哈希去重、图片解码和向 UI 发布。
///     具体来源是否读取、何时允许同曲替换由 IPlaybackSourceAdapter 决定。
/// </summary>
internal sealed class PlaybackCoverPipeline
{
    private const int CoverSize = 96;

    private IPlaybackSourceAdapter _sourceAdapter;
    private bool _enabled;
    private string _coverKey = string.Empty;
    private string _coverFingerprint = string.Empty;
    private bool _noValueNotified;

    public PlaybackCoverPipeline(IPlaybackSourceAdapter sourceAdapter)
    {
        _sourceAdapter = sourceAdapter;
    }

    /// <summary>当前封面或占位状态变化时发布；null 表示无封面。</summary>
    public event Action<string, BitmapSource?>? CoverChanged;

    /// <summary>切换播放源适配器，并丢弃上一来源的封面状态。</summary>
    public void SetAdapter(IPlaybackSourceAdapter sourceAdapter)
    {
        if (ReferenceEquals(_sourceAdapter, sourceAdapter))
            return;

        _sourceAdapter.ResetCoverState(_coverKey);
        _sourceAdapter = sourceAdapter;
        ResetState();
    }

    /// <summary>媒体区开关。关闭时释放适配器状态并通知 UI 收起封面。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
            return;

        _enabled = enabled;
        if (enabled)
            return;

        _sourceAdapter.ResetCoverState(_coverKey);
        ResetState();
        CoverChanged?.Invoke(string.Empty, null);
    }

    /// <summary>清空封面状态并通知 UI；用于会话断开。</summary>
    public void Clear()
    {
        _sourceAdapter.ResetCoverState(_coverKey);
        ResetState();
        CoverChanged?.Invoke(string.Empty, null);
    }

    /// <summary>
    ///     刷新指定歌曲的封面。过期请求直接丢弃，重复图片不重复发布。
    /// </summary>
    public async Task RefreshAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties props,
        string key,
        long nowUtcMs)
    {
        if (!_enabled)
            return;

        if (key != _coverKey)
        {
            _coverKey = key;
            _coverFingerprint = string.Empty;
            _noValueNotified = false;
        }

        try
        {
            var result = await _sourceAdapter.GetCoverAsync(
                new PlaybackCoverRequest(key, props, nowUtcMs),
                CancellationToken.None);

            if (key != _coverKey)
                return; // 等待期间又切歌，丢弃过期结果

            if (result.Action == PlaybackCoverAction.NoChange)
                return;

            if (result.Action == PlaybackCoverAction.NoCover)
            {
                if (_noValueNotified)
                    return;

                _noValueNotified = true;
                _coverFingerprint = string.Empty;
                CoverChanged?.Invoke(key, null);
                return;
            }

            if (result.Data is not { Length: > 0 } bytes)
                return;

            var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
            if (string.Equals(fingerprint, _coverFingerprint, StringComparison.Ordinal))
                return;

            using var ms = new MemoryStream(bytes, writable: false);

            // WPF 图片解码会读取 JPEG 内嵌 ICC，个别坏配置会直接失败。
            // GDI+ 解码更宽容，转换后再冻结成跨线程安全的 WPF BitmapSource。
            using var gdiImage = System.Drawing.Image.FromStream(ms);
            using var scaled = new System.Drawing.Bitmap(
                gdiImage,
                new System.Drawing.Size(CoverSize, CoverSize));
            var hBitmap = scaled.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(CoverSize, CoverSize));
                source.Freeze();

                _coverFingerprint = fingerprint;
                _noValueNotified = false;
                AppLog.Debug(
                    $"封面加载: 策略={_sourceAdapter.Key} key={key} " +
                    $"字节={bytes.Length} 哈希={fingerprint[..8]}");
                CoverChanged?.Invoke(key, source);
            }
            finally
            {
                NativeMethods.DeleteObject(hBitmap);
            }
        }
        catch (Exception ex)
        {
            if (key != _coverKey)
                return;

            _sourceAdapter.ResetCoverState(key);
            AppLog.Warn($"封面: 加载失败 key='{key}' [{ex.GetType().Name}] {ex.Message}");
            if (_coverFingerprint.Length == 0 && !_noValueNotified)
            {
                _noValueNotified = true;
                CoverChanged?.Invoke(key, null);
            }
        }
    }

    private void ResetState()
    {
        _coverKey = string.Empty;
        _coverFingerprint = string.Empty;
        _noValueNotified = false;
    }

}
