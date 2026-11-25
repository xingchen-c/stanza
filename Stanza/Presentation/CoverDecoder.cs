using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Stanza.Interop;

namespace Stanza.Presentation;

/// <summary>只负责原始字节到 WPF 图像的转换；曲目归属、重试及去重留在 Core 工作流。</summary>
internal static class CoverDecoder
{
    private const int CoverSize = 96;
    public static BitmapSource Decode(ImmutableArray<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        // GDI+ 容忍部分 JPEG 的坏 ICC，不能替换为 BitmapImage。
        using var image = System.Drawing.Image.FromStream(stream);
        using var scaled = new System.Drawing.Bitmap(image, new System.Drawing.Size(CoverSize, CoverSize));
        var handle = scaled.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(CoverSize, CoverSize));
            source.Freeze();
            return source;
        }
        finally { NativeMethods.DeleteObject(handle); }
    }
}
