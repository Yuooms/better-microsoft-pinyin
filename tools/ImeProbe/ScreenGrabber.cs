using System.Runtime.InteropServices;
using BetterPinyin.Interop.Windowing;

namespace BetterPinyin.Tools.ImeProbe;

/// <summary>
/// 极简全屏截图，直接走 GDI 的 BitBlt 并写出 32 位 BMP。
/// </summary>
/// <remarks>
/// 之所以不用 System.Drawing：那需要额外的 NuGet 包，而本项目希望保持「零第三方依赖」，
/// 以便在受限网络下也能直接构建。BMP 是无压缩格式，写文件头即可，无需编码器。
/// </remarks>
internal static class ScreenGrabber
{
    private const int SRCCOPY = 0x00CC0020;
    private const int DIB_RGB_COLORS = 0;
    private const int BI_RGB = 0;

    /// <summary>PrintWindow 的标志：渲染完整内容。缺它会画出空白（WinUI / UWP 尤其明显）。</summary>
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    /// <summary>PatBlt 的黑色填充光栅操作码。</summary>
    private const uint BLACKNESS = 0x00000042;

    /// <summary>抓取整个虚拟桌面并保存为 BMP。</summary>
    /// <param name="path">输出文件路径。</param>
    /// <param name="width">桌面宽度。</param>
    /// <param name="height">桌面高度。</param>
    /// <returns>是否成功。</returns>
    public static bool CaptureVirtualScreen(string path, out int width, out int height, int downscale = 1)
    {
        // SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
        // SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79
        width = GetSystemMetrics(78);
        height = GetSystemMetrics(79);
        var originX = GetSystemMetrics(76);
        var originY = GetSystemMetrics(77);

        return Capture(path, originX, originY, width, height, downscale, out width, out height);
    }

    /// <summary>
    /// 抓取屏幕上的指定区域并按整数倍缩小后保存为 BMP。
    /// </summary>
    /// <param name="path">输出文件路径。</param>
    /// <param name="x">区域左上角 X（虚拟桌面坐标）。</param>
    /// <param name="y">区域左上角 Y（虚拟桌面坐标）。</param>
    /// <param name="width">区域宽度。</param>
    /// <param name="height">区域高度。</param>
    /// <param name="downscale">缩小倍数，1 表示不缩放；用最近邻采样，够用且无依赖。</param>
    /// <param name="outputWidth">实际输出宽度。</param>
    /// <param name="outputHeight">实际输出高度。</param>
    /// <returns>是否成功。</returns>
    public static bool Capture(
        string path,
        int x,
        int y,
        int width,
        int height,
        int downscale,
        out int outputWidth,
        out int outputHeight)
    {
        outputWidth = 0;
        outputHeight = 0;

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var scale = Math.Max(1, downscale);
        outputWidth = Math.Max(1, width / scale);
        outputHeight = Math.Max(1, height / scale);

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        var ok = false;

        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                return false;
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = 40,
                    Width = width,
                    // 负高度表示自上而下存储，省一次整块翻转。
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BI_RGB,
                    SizeImage = width * height * 4,
                },
            };

            dib = CreateDIBSection(memoryDc, ref info, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return false;
            }

            previous = SelectObject(memoryDc, dib);
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SRCCOPY))
            {
                return false;
            }

            var sourceLength = width * height * 4;
            var source = new byte[sourceLength];
            Marshal.Copy(bits, source, 0, sourceLength);

            var pixels = scale == 1 ? source : Downscale(source, width, height, scale, outputWidth, outputHeight);
            WriteBmp(path, outputWidth, outputHeight, pixels);
            ok = true;
        }
        catch (Exception)
        {
            ok = false;
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                SelectObject(memoryDc, previous);
            }

            if (dib != IntPtr.Zero)
            {
                DeleteObject(dib);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            ReleaseDC(IntPtr.Zero, screenDc);
        }

        return ok;
    }

    /// <summary>
    /// 让窗口把自己画到内存里再保存，不依赖屏幕像素。
    /// </summary>
    /// <param name="handle">窗口句柄。</param>
    /// <param name="path">输出文件路径。</param>
    /// <param name="downscale">缩小倍数，1 表示不缩放。</param>
    /// <param name="outputWidth">实际输出宽度。</param>
    /// <param name="outputHeight">实际输出高度。</param>
    /// <returns>是否成功。</returns>
    /// <remarks>
    /// <para>和 <see cref="Capture"/> 的区别：那个走屏幕 DC 的 BitBlt，窗口被挡住或最小化时
    /// 抓到的就是挡住它的东西；这里用 <c>PrintWindow</c> 让窗口自己响应 <c>WM_PRINT</c>，
    /// 前后台、是否遮挡都不影响，也不改变前台窗口。</para>
    /// <para><c>PW_RENDERFULLCONTENT</c>(2) 是关键：没有它，WinUI / UWP / 部分自绘窗口
    /// 只会画出空白。拿不到内容时按「整幅全黑」判定失败，返回 false 让调用方决定是否退回屏幕抓取。</para>
    /// </remarks>
    public static bool CaptureWindow(
        IntPtr handle,
        string path,
        int downscale,
        out int outputWidth,
        out int outputHeight)
    {
        outputWidth = 0;
        outputHeight = 0;

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (!TryGetVisibleBounds(handle, out var rect))
        {
            return false;
        }

        var width = rect.Width;
        var height = rect.Height;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var scale = Math.Max(1, downscale);
        outputWidth = Math.Max(1, width / scale);
        outputHeight = Math.Max(1, height / scale);

        var windowDc = GetWindowDC(handle);
        if (windowDc == IntPtr.Zero)
        {
            return false;
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        var ok = false;

        try
        {
            memoryDc = CreateCompatibleDC(windowDc);
            if (memoryDc == IntPtr.Zero)
            {
                return false;
            }

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = 40,
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BI_RGB,
                    SizeImage = width * height * 4,
                },
            };

            dib = CreateDIBSection(memoryDc, ref info, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return false;
            }

            previous = SelectObject(memoryDc, dib);

            // 先把目标区域填成黑色：PrintWindow 只画它负责的部分，
            // 不预填的话未绘制区域是未初始化内存，无法判断"是不是真的没画出来"。
            PatBlt(memoryDc, 0, 0, width, height, BLACKNESS);

            var printed = PrintWindow(handle, memoryDc, PW_RENDERFULLCONTENT);
            var sourceLength = width * height * 4;
            var source = new byte[sourceLength];
            Marshal.Copy(bits, source, 0, sourceLength);

            if (!printed || IsAllBlack(source))
            {
                // 窗口不响应 WM_PRINT（典型：同为分层窗口的自绘浮窗）。
                // 退回窗口 DC 的 BitBlt：可见部分仍然能拿到，总比全黑好。
                if (!BitBlt(memoryDc, 0, 0, width, height, windowDc, 0, 0, SRCCOPY))
                {
                    return false;
                }

                Marshal.Copy(bits, source, 0, sourceLength);
                if (IsAllBlack(source))
                {
                    return false;
                }
            }

            var pixels = scale == 1 ? source : Downscale(source, width, height, scale, outputWidth, outputHeight);
            WriteBmp(path, outputWidth, outputHeight, pixels);
            ok = true;
        }
        catch (Exception)
        {
            ok = false;
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                SelectObject(memoryDc, previous);
            }

            if (dib != IntPtr.Zero)
            {
                DeleteObject(dib);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            ReleaseDC(handle, windowDc);
        }

        return ok;
    }

    /// <summary>
    /// 取窗口的可见边界：优先用 DWM 的扩展边框，避开窗口外那圈透明的缩放手柄。
    /// </summary>
    /// <param name="handle">窗口句柄。</param>
    /// <param name="rect">输出矩形（屏幕坐标）。</param>
    /// <returns>是否成功。</returns>
    private static bool TryGetVisibleBounds(IntPtr handle, out NativeRect rect)
    {
        const int DwmwaExtendedFrameBounds = 9;

        if (DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out var bounds, Marshal.SizeOf<NativeRect>()) == 0 &&
            bounds.Width > 0 &&
            bounds.Height > 0)
        {
            rect = bounds;
            return true;
        }

        if (GetWindowRect(handle, out rect))
        {
            return rect.Width > 0 && rect.Height > 0;
        }

        rect = default;
        return false;
    }

    private static bool IsAllBlack(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Downscale(byte[] source, int width, int height, int scale, int outWidth, int outHeight)
    {
        var target = new byte[outWidth * outHeight * 4];
        for (var y = 0; y < outHeight; y++)
        {
            var sourceRow = y * scale * width * 4;
            var targetRow = y * outWidth * 4;
            for (var x = 0; x < outWidth; x++)
            {
                var sourceIndex = sourceRow + (x * scale * 4);
                var targetIndex = targetRow + (x * 4);
                target[targetIndex] = source[sourceIndex];
                target[targetIndex + 1] = source[sourceIndex + 1];
                target[targetIndex + 2] = source[sourceIndex + 2];
                target[targetIndex + 3] = 255;
            }
        }

        return target;
    }

    private static void WriteBmp(string path, int width, int height, byte[] pixels)
    {
        var pixelBytes = pixels.Length;
        var fileSize = 14 + 40 + pixelBytes;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        // BITMAPFILEHEADER
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);
        writer.Write(0);
        writer.Write(14 + 40);

        // BITMAPINFOHEADER
        writer.Write(40);
        writer.Write(width);
        writer.Write(-height);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(BI_RGB);
        writer.Write(pixelBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        writer.Write(pixels);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    /// <summary>拿窗口自己的 DC（含非客户区），用于 PrintWindow 失败时的退路。</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    /// <summary>请求窗口把自身内容画到给定 DC 上。</summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="hdcBlt">目标 DC。</param>
    /// <param name="flags">1 = 含非客户区，2 = 渲染完整内容（Win8.1+）。</param>
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out NativeRect value, int size);

    /// <summary>用当前画刷填充矩形；这里只用 BLACKNESS 做预填。</summary>
    [DllImport("gdi32.dll")]
    private static extern bool PatBlt(IntPtr hDc, int x, int y, int width, int height, uint rop);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hDc,
        ref BitmapInfo pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int width,
        int height,
        IntPtr hdcSrc,
        int xSrc,
        int ySrc,
        int rop);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
    }
}
