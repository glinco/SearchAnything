using System;
using System.Drawing;
using SearchAnything;

namespace VisionSpike;

internal static class ImageOps
{
    /// <summary>
    /// Resizes one region of the screenshot to size x size and writes it as a normalized CHW float tensor at
    /// <paramref name="offset"/>. By default the region is stretched to fill the square (SigLIP style). With
    /// <paramref name="padSquare"/> it keeps its aspect ratio, anchored top-left, and the rest is mid-gray
    /// (OWL-ViT style). Shrinking averages every source pixel under each output pixel, so there is no aliasing;
    /// enlarging samples bilinearly. Pure managed code, so many crops can be prepared in parallel.
    /// </summary>
    public static void WriteTensor(CapturedScreen screen, Rectangle region, int size, bool padSquare,
                                   float[] mean, float[] std, float[] tensor, int offset)
    {
        byte[] bgra = screen.Pixels;
        int width = screen.Width;
        var inside = Rectangle.Intersect(region, new Rectangle(0, 0, screen.Width, screen.Height));
        double sx = (double)(padSquare ? Math.Max(region.Width, region.Height) : region.Width) / size;
        double sy = padSquare ? sx : (double)region.Height / size;
        int plane = size * size;

        for (int oy = 0; oy < size; oy++)
        {
            double y0 = region.Y + oy * sy, y1 = y0 + sy;
            for (int ox = 0; ox < size; ox++)
            {
                double x0 = region.X + ox * sx, x1 = x0 + sx;
                double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
                float r = 0.5f, g = 0.5f, b = 0.5f;
                if (cx < inside.Left || cx >= inside.Right || cy < inside.Top || cy >= inside.Bottom)
                {
                    // Padding, or a region that runs past the screen edge: leave it gray.
                }
                else if (sx >= 1 && sy >= 1)
                {
                    int ix0 = Math.Max((int)x0, inside.Left), ix1 = Math.Clamp((int)Math.Ceiling(x1), ix0 + 1, inside.Right);
                    int iy0 = Math.Max((int)y0, inside.Top), iy1 = Math.Clamp((int)Math.Ceiling(y1), iy0 + 1, inside.Bottom);
                    long sr = 0, sg = 0, sb = 0;
                    for (int y = iy0; y < iy1; y++)
                        for (int x = ix0; x < ix1; x++)
                        {
                            int i = (y * width + x) * 4;
                            sb += bgra[i];
                            sg += bgra[i + 1];
                            sr += bgra[i + 2];
                        }
                    float n = (ix1 - ix0) * (iy1 - iy0) * 255f;
                    (r, g, b) = (sr / n, sg / n, sb / n);
                }
                else
                {
                    double fx = Math.Clamp(cx - 0.5, inside.Left, inside.Right - 1), fy = Math.Clamp(cy - 0.5, inside.Top, inside.Bottom - 1);
                    int xa = (int)fx, ya = (int)fy, xb = Math.Min(xa + 1, inside.Right - 1), yb = Math.Min(ya + 1, inside.Bottom - 1);
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
}
