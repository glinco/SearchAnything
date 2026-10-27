using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace VisionSpike;

/// <summary>
/// Candidate regions to score with an image-text model. Screens are mostly flat backgrounds, text and
/// rectangular pictures, so: mark small blocks that have visible texture, ignore the ones OCR says are text,
/// and take the connected areas that remain (photos, charts, maps, QR codes). A wall of thumbnails with thin
/// gutters is cut into one region per thumbnail; other big areas are cut into windows. A fixed coarse grid
/// covers anything else, including mostly-text concepts like "table" or "source code".
/// </summary>
/// <summary>The regions to score, and which of them are pictures that highlights should snap to.</summary>
internal sealed record RegionSet(List<Rectangle> Regions, List<Rectangle> Pictures);

internal static class Proposals
{
    private const int Block = 4;
    private const int MinSide = 40;
    private const int EdgeContrast = 24;

    public static RegionSet Find(byte[] bgra, int width, int height, IEnumerable<Rectangle> textBoxes, int maxRegions, bool splitGrids)
    {
        int bw = width / Block, bh = height / Block;
        var busy = new bool[bw, bh];
        var mean = new int[bw, bh];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int min = 255, max = 0, sum = 0;
                for (int y = by * Block; y < by * Block + Block; y++)
                    for (int x = bx * Block; x < bx * Block + Block; x++)
                    {
                        int i = (y * width + x) * 4;
                        int lum = (bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8;
                        min = Math.Min(min, lum);
                        max = Math.Max(max, lum);
                        sum += lum;
                    }
                busy[bx, by] = max - min > 10;
                mean[bx, by] = sum / (Block * Block);
            }

        // A strong edge that falls exactly between two blocks leaves both looking flat. Mark the side that isn't
        // page background, so a photo with a smooth sky still gets its outline (and so its whole rectangle, not
        // just its textured parts), while the gutters between thumbnails stay empty. The bar is high enough that
        // subtle panel borders (light gray on white) don't count.
        var histogram = new int[256];
        foreach (int m in mean)
            histogram[m]++;
        int background = Array.IndexOf(histogram, histogram.Max());
        void MarkEdge(int ax, int ay, int bx2, int by2)
        {
            if (Math.Abs(mean[ax, ay] - mean[bx2, by2]) <= EdgeContrast)
                return;
            if (Math.Abs(mean[ax, ay] - background) >= Math.Abs(mean[bx2, by2] - background))
                busy[ax, ay] = true;
            else
                busy[bx2, by2] = true;
        }
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                if (bx + 1 < bw) MarkEdge(bx, by, bx + 1, by);
                if (by + 1 < bh) MarkEdge(bx, by, bx, by + 1);
            }

        foreach (var t in textBoxes)
        {
            var r = Rectangle.Inflate(t, 3, 3);
            for (int by = Math.Max(0, r.Top / Block); by <= Math.Min(bh - 1, r.Bottom / Block); by++)
                for (int bx = Math.Max(0, r.Left / Block); bx <= Math.Min(bw - 1, r.Right / Block); bx++)
                    busy[bx, by] = false;
        }

        var pictures = Components(busy, bw, bh)
            .Where(r => r.Width >= MinSide && r.Height >= MinSide)
            .OrderByDescending(r => r.Width * r.Height)
            .ToList();

        var regions = new List<Rectangle>();
        void Add(Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, width, height));
            if (r.Width >= MinSide && r.Height >= MinSide && !regions.Any(x => Similar(x, r)))
                regions.Add(r);
        }

        // Coarse grid first: always there, so every query has something to compare against.
        Add(new Rectangle(0, 0, width, height));
        foreach (var (cols, rows) in new[] { (2, 2), (3, 3) })
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    Add(new Rectangle(c * width / cols, r * height / rows, width / cols, height / rows));

        foreach (var p in pictures)
            Add(p);

        // A picture that is really a wall of thumbnails gets one region per thumbnail instead of windows.
        var grids = new HashSet<Rectangle>();
        var cells = new List<Rectangle>();
        if (splitGrids)
            foreach (var p in pictures)
            {
                var split = Grid(bgra, width, p, vertical: true, minPieces: 3);
                if (split.Count == 1)
                    split = Grid(bgra, width, p, vertical: false, minPieces: 3);
                if (split.Count == 1)
                    continue;
                grids.Add(p);
                cells.AddRange(split);
                foreach (var cell in split)
                    Add(cell);
            }

        // A container (a slide, a card, a spreadsheet) is mostly one flat color around other pictures it holds.
        // Those pictures are regions of their own, so containers get no windows and highlights never snap to
        // them. Both conditions matter: a chart is mostly flat but holds nothing, and a photo with a smooth sky
        // may hold a textured band, but a gradient isn't one flat color.
        bool IsContainer(Rectangle p) =>
            MostlyFlat(p) && pictures.Any(q => q != p && Geometry.Area(q) >= 0.02 * Geometry.Area(p)
                                               && Geometry.Intersection(p, q) >= 0.9 * Geometry.Area(q));
        bool MostlyFlat(Rectangle p)
        {
            var counts = new Dictionary<int, int>();
            int blocks = 0;
            for (int by = p.Top / Block; by < Math.Min(bh, p.Bottom / Block); by++)
                for (int bx = p.Left / Block; bx < Math.Min(bw, p.Right / Block); bx++)
                {
                    blocks++;
                    if (!busy[bx, by])
                        counts[mean[bx, by]] = counts.GetValueOrDefault(mean[bx, by]) + 1;
                }
            if (blocks == 0 || counts.Count == 0)
                return false;
            int mode = counts.MaxBy(c => c.Value).Key;
            int flat = counts.Where(c => Math.Abs(c.Key - mode) <= 2).Sum(c => c.Value);
            return flat >= 0.4 * blocks;
        }
        var containers = pictures.Where(IsContainer).ToHashSet();

        // Cut big pictures into windows about a third of their short side, so things inside them can stand out.
        foreach (var p in pictures.Where(p => Math.Min(p.Width, p.Height) >= 360 && !grids.Contains(p) && !containers.Contains(p)))
        {
            int side = Math.Max(180, Math.Min(p.Width, p.Height) / 3);
            for (int y = p.Top; y + side / 2 < p.Bottom; y += side)
                for (int x = p.Left; x + side / 2 < p.Right; x += side)
                    Add(new Rectangle(x, y, side, side));
        }

        // Highlights snap to pictures and thumbnails, never to a wall of thumbnails or another container.
        var targets = pictures.Where(p => !grids.Contains(p) && !containers.Contains(p)).Concat(cells).ToList();
        return new RegionSet(regions.Take(maxRegions).ToList(), targets);
    }

    /// <summary>
    /// Cuts a region along its gutters: full-length columns (or rows) of near-uniform pixels. The cut only
    /// counts if it leaves at least <paramref name="minPieces"/> pieces of similar size, so a photo with a band
    /// of clear sky or a white margin is not cut up. Each piece is then tried along the other axis, where two
    /// pieces are enough because the grid is already established. Returns the region itself if it isn't a grid.
    /// </summary>
    private static List<Rectangle> Grid(byte[] bgra, int width, Rectangle r, bool vertical, int minPieces)
    {
        int length = vertical ? r.Width : r.Height;
        var pieces = new List<(int Start, int End)>();
        int start = -1;
        for (int k = 0; k <= length; k++)
        {
            bool gutter = k == length || UniformLine(bgra, width, r, vertical, k);
            if (!gutter && start < 0)
                start = k;
            else if (gutter && start >= 0)
            {
                if (k - start >= MinSide)
                    pieces.Add((start, k));
                start = -1;
            }
        }
        if (pieces.Count < minPieces)
            return new List<Rectangle> { r };
        int shortest = pieces.Min(p => p.End - p.Start), longest = pieces.Max(p => p.End - p.Start);
        if (longest > 2 * shortest)
            return new List<Rectangle> { r };

        var cells = new List<Rectangle>();
        foreach (var (s, e) in pieces)
        {
            var piece = vertical
                ? new Rectangle(r.X + s, r.Y, e - s, r.Height)
                : new Rectangle(r.X, r.Y + s, r.Width, e - s);
            cells.AddRange(Grid(bgra, width, piece, !vertical, minPieces: 2) is { Count: > 1 } inner ? inner : new() { piece });
        }
        return cells;
    }

    /// <summary>True if column (or row) k of the region varies by no more than a few luminance levels.</summary>
    private static bool UniformLine(byte[] bgra, int width, Rectangle r, bool vertical, int k)
    {
        int min = 255, max = 0;
        int count = vertical ? r.Height : r.Width;
        for (int j = 0; j < count; j++)
        {
            int x = vertical ? r.X + k : r.X + j, y = vertical ? r.Y + j : r.Y + k;
            int i = (y * width + x) * 4;
            int lum = (bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8;
            if (lum < min) min = lum;
            if (lum > max) max = lum;
            if (max - min > 12)
                return false;
        }
        return true;
    }

    private static bool Similar(Rectangle a, Rectangle b)
    {
        var i = Rectangle.Intersect(a, b);
        double inter = (double)i.Width * i.Height;
        return inter / ((double)a.Width * a.Height + (double)b.Width * b.Height - inter) > 0.8;
    }

    /// <summary>Bounding boxes, in pixels, of 8-connected groups of busy blocks.</summary>
    private static IEnumerable<Rectangle> Components(bool[,] busy, int bw, int bh)
    {
        var seen = new bool[bw, bh];
        var stack = new Stack<(int, int)>();
        for (int y0 = 0; y0 < bh; y0++)
            for (int x0 = 0; x0 < bw; x0++)
            {
                if (!busy[x0, y0] || seen[x0, y0])
                    continue;
                int minX = x0, maxX = x0, minY = y0, maxY = y0, count = 0;
                stack.Push((x0, y0));
                seen[x0, y0] = true;
                while (stack.Count > 0)
                {
                    var (x, y) = stack.Pop();
                    count++;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < bw && ny < bh && busy[nx, ny] && !seen[nx, ny])
                            {
                                seen[nx, ny] = true;
                                stack.Push((nx, ny));
                            }
                        }
                }
                // No density test: flat charts are mostly empty inside their outline. Lone divider lines are
                // dropped later for being thinner than MinSide; card outlines become card-sized regions, which is fine.
                yield return new Rectangle(minX * Block, minY * Block, (maxX - minX + 1) * Block, (maxY - minY + 1) * Block);
            }
    }
}
