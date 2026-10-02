namespace BetterPerformance.Core
{
    // Measured spend of the budget's rebuilds in one late batch. Ms is the plan's demotion input;
    // the over-budget count and the max are folded in once per frame. docs/heightmap-rebuild-budget.md.
    public sealed class HeightmapFrameSpend
    {
        private int frame = -1;
        private long framesOverBudget;
        private double maxMs;

        public double Ms { get; private set; }
        // A frame's spend is open and not yet folded.
        public bool Pending => frame >= 0;

        // A new late batch: folds the previous one, then counts from zero.
        public void Open(int frameIndex, double budgetMs)
        {
            Fold(budgetMs);
            frame = frameIndex;
            Ms = 0;
        }

        public void Add(double ms) => Ms += ms;

        // Folds the open frame unless it is still the current one. Sampling runs in Update, before
        // that frame's late batch, so any other frame is complete.
        public void CloseBefore(int currentFrame, double budgetMs)
        {
            if (frame >= 0 && frame != currentFrame) Fold(budgetMs);
        }

        // Final export: a stop after the late batch (plugin shutdown) would otherwise drop the frame
        // still marked current. Never called inside the batch, so the open frame is complete.
        public void Close(double budgetMs) => Fold(budgetMs);

        private void Fold(double budgetMs)
        {
            if (frame < 0) return;
            if (Ms > budgetMs) framesOverBudget++;
            if (Ms > maxMs) maxMs = Ms;
            frame = -1;
        }

        public long TakeFramesOverBudget()
        {
            long value = framesOverBudget;
            framesOverBudget = 0;
            return value;
        }

        public double TakeMaxMs()
        {
            double value = maxMs;
            maxMs = 0;
            return value;
        }

        // Drops the open frame too, so it never leaks into the next capture.
        public void Reset()
        {
            frame = -1;
            Ms = 0;
            framesOverBudget = 0;
            maxMs = 0;
        }
    }
}
