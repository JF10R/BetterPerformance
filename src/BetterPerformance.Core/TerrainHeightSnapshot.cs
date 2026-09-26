using System.Runtime.InteropServices;

namespace BetterPerformance.Core
{
    // TerrainComp keeps its height edits in three parallel arrays (modified flag, level delta, smooth
    // delta). Two states give the same heights, hence the same meshes, when all three are equal,
    // compared bit for bit. docs/terrain-paint-only-reload.md.
    public static class TerrainHeightSnapshot
    {
        public static bool Equal(bool[] oldModified, float[] oldLevel, float[] oldSmooth, bool[] modified, float[] level, float[] smooth)
        {
            if (oldModified == null || oldLevel == null || oldSmooth == null || modified == null || level == null || smooth == null) return false;
            int n = modified.Length;
            if (oldModified.Length != n || oldLevel.Length != level.Length || oldSmooth.Length != smooth.Length || level.Length != n || smooth.Length != n) return false;
            for (int i = 0; i < n; i++)
            {
                if (oldModified[i] != modified[i]) return false;
                // Bit equality: NaN == NaN and -0 != +0, so a change the renderer could see is never missed.
                if (Bits(oldLevel[i]) != Bits(level[i]) || Bits(oldSmooth[i]) != Bits(smooth[i])) return false;
            }
            return true;
        }

        // BitConverter.SingleToInt32Bits is not in .NET Framework 4.7.2.
        private static int Bits(float value) => new FloatBits { Value = value }.Raw;

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Value;
            [FieldOffset(0)] public int Raw;
        }
    }
}
