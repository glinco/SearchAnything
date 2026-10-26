using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SearchAnything;

/// <summary>A captured image as tightly packed BGRA pixels (stride = width * 4).</summary>
internal sealed record CapturedScreen(byte[] Pixels, int Width, int Height, NativeMethods.RECT Bounds)
{
    public int Stride => Width * 4;
}

internal static class ScreenCapture
{
    /// <summary>Bounds, in physical pixels, of the monitor the mouse is on.</summary>
    public static NativeMethods.RECT MonitorUnderCursor()
    {
        NativeMethods.GetCursorPos(out var cursor);
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(monitor, ref info);
        return info.rcMonitor;
    }

    public static CapturedScreen Grab(NativeMethods.RECT bounds)
    {
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
        return new CapturedScreen(ToBgra(bitmap), bounds.Width, bounds.Height, bounds);
    }

    public static CapturedScreen Load(string path)
    {
        using var image = Image.FromFile(path);
        using var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
            g.DrawImage(image, 0, 0, image.Width, image.Height);
        var bounds = new NativeMethods.RECT { Right = image.Width, Bottom = image.Height };
        return new CapturedScreen(ToBgra(bitmap), image.Width, image.Height, bounds);
    }

    /// <summary>Returns a resized copy of the pixels (used to fit or enlarge text for OCR).</summary>
    public static (byte[] Pixels, int Width, int Height) Resize(CapturedScreen source, double scale)
    {
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var handle = GCHandle.Alloc(source.Pixels, GCHandleType.Pinned);
        try
        {
            using var src = new Bitmap(source.Width, source.Height, source.Stride, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            using var dst = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, width, height));
            }
            return (ToBgra(dst), width, height);
        }
        finally
        {
            handle.Free();
        }
    }

    private static byte[] ToBgra(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = bitmap.Width * 4;
            var pixels = new byte[rowBytes * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * rowBytes, rowBytes);

            // GDI often leaves the alpha channel at 0 for screen copies; force everything opaque.
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
