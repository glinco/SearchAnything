using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VisionSpike;

internal static class ImageOps
{
    /// <summary>
    /// Stretches one region of tightly packed BGRA pixels to size x size and writes it as a normalized CHW
    /// float tensor. Shrinking averages every source pixel under the output pixel (no aliasing); enlarging
    /// samples bilinearly. Pure managed code, so many crops can be prepared in parallel.
    /// </summary>
    public static void WriteTensor(byte[] bgra, int width, int height, Rectangle region, int size,
                                   float[] mean, float[] std, float[] tensor, int offset)
    {
        int plane = size * size;
        double sx = (double)region.Width / size, sy = (double)region.Height / size;
        for (int oy = 0; oy < size; oy++)
        {
            double y0 = region.Y + oy * sy, y1 = y0 + sy;
            for (int ox = 0; ox < size; ox++)
            {
                double x0 = region.X + ox * sx, x1 = x0 + sx;
                float r, g, b;
                if (sx >= 1 && sy >= 1)
                {
                    int ix0 = Math.Clamp((int)x0, 0, width - 1), ix1 = Math.Clamp((int)Math.Ceiling(x1), ix0 + 1, width);
                    int iy0 = Math.Clamp((int)y0, 0, height - 1), iy1 = Math.Clamp((int)Math.Ceiling(y1), iy0 + 1, height);
                    long sr = 0, sg = 0, sb = 0;
                    for (int y = iy0; y < iy1; y++)
                        for (int x = ix0; x < ix1; x++)
                        {
                            int i = (y * width + x) * 4;
                            sb += bgra[i]; sg += bgra[i + 1]; sr += bgra[i + 2];
                        }
                    float n = (ix1 - ix0) * (iy1 - iy0) * 255f;
                    (r, g, b) = (sr / n, sg / n, sb / n);
                }
                else
                {
                    double fx = Math.Clamp((x0 + x1) / 2 - 0.5, 0, width - 1), fy = Math.Clamp((y0 + y1) / 2 - 0.5, 0, height - 1);
                    int xa = (int)fx, ya = (int)fy, xb = Math.Min(xa + 1, width - 1), yb = Math.Min(ya + 1, height - 1);
                    double wx = fx - xa, wy = fy - ya;
                    float Sample(int c) => (float)(
                        (bgra[(ya * width + xa) * 4 + c] * (1 - wx) + bgra[(ya * width + xb) * 4 + c] * wx) * (1 - wy) +
                        (bgra[(yb * width + xa) * 4 + c] * (1 - wx) + bgra[(yb * width + xb) * 4 + c] * wx) * wy) / 255f;
                    (r, g, b) = (Sample(2), Sample(1), Sample(0));
                }
                int o = offset + oy * size + ox;
                tensor[o] = (r - mean[0]) / std[0];
                tensor[o + plane] = (g - mean[1]) / std[1];
                tensor[o + 2 * plane] = (b - mean[2]) / std[2];
            }
        }
    }

    /// <summary>
    /// Writes one region of the screenshot into a CHW float tensor at <paramref name="offset"/>, resized to
    /// size x size and normalized per channel. With <paramref name="padSquare"/> the region keeps its aspect
    /// ratio and the rest is filled with mid-gray (OWL-ViT style); otherwise it is stretched (SigLIP style).
    /// Regions may extend past the screen edge; that part is also gray.
    /// </summary>
    public static void WriteTensor(Bitmap screen, Rectangle region, int size, float[] mean, float[] std,
                                   bool padSquare, InterpolationMode interpolation, float[] tensor, int offset)
    {
        using var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.Clear(Color.FromArgb(128, 128, 128));
            g.InterpolationMode = interpolation;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges

            var visible = Rectangle.Intersect(region, new Rectangle(0, 0, screen.Width, screen.Height));
            double sx, sy;
            if (padSquare)
            {
                double side = Math.Max(region.Width, region.Height);
                sx = sy = size / side;
            }
            else
            {
                sx = (double)size / region.Width;
                sy = (double)size / region.Height;
            }
            var dest = new Rectangle(
                (int)Math.Round((visible.X - region.X) * sx), (int)Math.Round((visible.Y - region.Y) * sy),
                (int)Math.Round(visible.Width * sx), (int)Math.Round(visible.Height * sy));
            g.DrawImage(screen, dest, visible.X, visible.Y, visible.Width, visible.Height, GraphicsUnit.Pixel, attributes);
        }

        var data = dst.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[size * 4];
            int plane = size * size;
            for (int y = 0; y < size; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < size; x++)
                {
                    int i = offset + y * size + x;
                    tensor[i] = (row[x * 4 + 2] / 255f - mean[0]) / std[0];
                    tensor[i + plane] = (row[x * 4 + 1] / 255f - mean[1]) / std[1];
                    tensor[i + 2 * plane] = (row[x * 4] / 255f - mean[2]) / std[2];
                }
            }
        }
        finally
        {
            dst.UnlockBits(data);
        }
    }
}
