using System.Collections.Generic;
using System.Windows;

namespace SearchAnything;

internal static class RectMerge
{
    /// <summary>
    /// Replaces every group of overlapping rectangles with their combined bounding box.
    /// Used for the spotlight holes: with the even-odd fill rule, two overlapping holes would cancel out.
    /// </summary>
    public static List<Rect> MergeOverlapping(IEnumerable<Rect> rects)
    {
        var merged = new List<Rect>();
        foreach (var rect in rects)
        {
            var current = rect;
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int i = merged.Count - 1; i >= 0; i--)
                {
                    if (!merged[i].IntersectsWith(current))
                        continue;
                    current.Union(merged[i]);
                    merged.RemoveAt(i);
                    grew = true;
                }
            }
            merged.Add(current);
        }
        return merged;
    }
}
