using System;

namespace BetterPerformance.Core
{
    public struct FrameStepSnapshot
    {
        public long FramesObserved;
        public long FixedStepsTotal;
        public long MaxFixedStepsPerFrame;
        public long FramesWithMultipleFixedSteps;
        public long FramesWithoutFixedStep;
        // Steps already observed for a frame whose rendered update has not been seen yet.
        public long PendingFixedSteps;
    }

    // Unity runs every catch-up FixedUpdate of a frame before that frame's Update, so
    // steps accumulate into the pending counter and are attributed when the frame arrives.
    // Main thread only; no locking, no allocation after construction.
    public sealed class FrameStepWindow
    {
        private long frames, steps, maxPerFrame, multiple, without, pending;

        public void Reset() { frames = steps = maxPerFrame = multiple = without = pending = 0; }

        public void NoteFixedStep()
        {
            if (pending < long.MaxValue) pending++;
        }

        public void NoteFrame()
        {
            frames++;
            steps += pending;
            if (pending > maxPerFrame) maxPerFrame = pending;
            if (pending > 1) multiple++;
            if (pending == 0) without++;
            pending = 0;
        }

        // Interval aggregates reset; an unattributed partial frame is carried, never counted twice.
        public FrameStepSnapshot Drain()
        {
            var snapshot = new FrameStepSnapshot
            {
                FramesObserved = frames,
                FixedStepsTotal = steps,
                MaxFixedStepsPerFrame = maxPerFrame,
                FramesWithMultipleFixedSteps = multiple,
                FramesWithoutFixedStep = without,
                PendingFixedSteps = pending
            };
            frames = steps = maxPerFrame = multiple = without = 0;
            return snapshot;
        }
    }
}
