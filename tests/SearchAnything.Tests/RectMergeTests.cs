using System.Windows;
using Xunit;

namespace SearchAnything.Tests;

public class RectMergeTests
{
    [Fact]
    public void Separate_rectangles_stay_separate()
    {
        var merged = RectMerge.MergeOverlapping(new[] { new Rect(0, 0, 10, 10), new Rect(50, 50, 10, 10) });

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Overlapping_rectangles_become_one()
    {
        var merged = RectMerge.MergeOverlapping(new[] { new Rect(0, 0, 10, 10), new Rect(5, 0, 10, 10) });

        Assert.Equal(new Rect(0, 0, 15, 10), Assert.Single(merged));
    }

    [Fact]
    public void Chains_of_overlaps_merge_completely()
    {
        // A overlaps B and B overlaps C, but A and C don't touch; all three must still merge.
        var merged = RectMerge.MergeOverlapping(new[]
        {
            new Rect(0, 0, 10, 10),
            new Rect(30, 0, 10, 10),
            new Rect(8, 0, 25, 10),
        });

        Assert.Equal(new Rect(0, 0, 40, 10), Assert.Single(merged));
    }
}
