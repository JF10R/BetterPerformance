namespace BetterPerformance.Core
{
    public static class CaptureMarkers
    {
        public static bool CrossesBoundary(long loopStart, long loopEnd, long marker) =>
            loopStart < marker && marker <= loopEnd;

        public static bool IsValid(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 48) return false;
            foreach (char c in name)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                    !(c >= '0' && c <= '9') && c != '_' && c != '-') return false;
            return true;
        }
    }
}
