using System;
using BetterPerformance.Core;

internal static class TerrainHeightSnapshotTests
{
    public static void Run()
    {
        const int n = 65 * 65;
        bool[] modified = new bool[n];
        float[] level = new float[n], smooth = new float[n];
        modified[100] = true; level[100] = 1.25f; smooth[100] = -0.5f;
        bool[] m2 = (bool[])modified.Clone();
        float[] l2 = (float[])level.Clone(), s2 = (float[])smooth.Clone();
        Check(TerrainHeightSnapshot.Equal(modified, level, smooth, m2, l2, s2), "identical heights: a paint-only edit");

        m2[200] = true;
        Check(!TerrainHeightSnapshot.Equal(modified, level, smooth, m2, l2, s2), "a newly modified vertex is a height change");
        m2[200] = false; l2[100] = 1.2500001f;
        Check(!TerrainHeightSnapshot.Equal(modified, level, smooth, m2, l2, s2), "the smallest level change is seen");
        l2[100] = 1.25f; s2[n - 1] = -0f;
        Check(!TerrainHeightSnapshot.Equal(modified, level, smooth, m2, l2, s2), "-0 against +0 counts as a change: never guess");
        s2[n - 1] = 0f; level[5] = float.NaN; l2[5] = float.NaN;
        Check(TerrainHeightSnapshot.Equal(modified, level, smooth, m2, l2, s2), "the same NaN bits are equal");

        Check(!TerrainHeightSnapshot.Equal(modified, level, smooth, new bool[n - 1], l2, s2), "a size change is a change");
        Check(!TerrainHeightSnapshot.Equal(null!, level, smooth, m2, l2, s2), "a missing snapshot is never equal");
        Check(!TerrainHeightSnapshot.Equal(modified, level, smooth, m2, null!, s2), "a missing array is never equal");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
