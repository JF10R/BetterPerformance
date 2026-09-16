using System;
using BetterPerformance.Core;

internal static class FrameStepWindowTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static void Run()
    {
        var window = new FrameStepWindow();
        FrameStepSnapshot empty = window.Drain();
        Check(empty.FramesObserved == 0 && empty.FixedStepsTotal == 0 && empty.MaxFixedStepsPerFrame == 0
            && empty.FramesWithMultipleFixedSteps == 0 && empty.FramesWithoutFixedStep == 0
            && empty.PendingFixedSteps == 0, "An interval without callbacks must not fabricate frames or steps.");

        window.NoteFixedStep();
        window.NoteFrame();
        window.NoteFixedStep();
        window.NoteFixedStep();
        window.NoteFixedStep();
        window.NoteFrame();
        window.NoteFrame();
        FrameStepSnapshot observed = window.Drain();
        Check(observed.FramesObserved == 3 && observed.FixedStepsTotal == 4,
            "Every fixed step preceding a frame belongs to that frame exactly once.");
        Check(observed.MaxFixedStepsPerFrame == 3 && observed.FramesWithMultipleFixedSteps == 1,
            "A catch-up storm must be visible as one frame carrying several steps.");
        Check(observed.FramesWithoutFixedStep == 1, "A frame with no simulation step is distinguishable from a missing frame.");
        Check(observed.PendingFixedSteps == 0, "No partial frame is outstanding here.");

        FrameStepSnapshot drained = window.Drain();
        Check(drained.FramesObserved == 0 && drained.FixedStepsTotal == 0 && drained.MaxFixedStepsPerFrame == 0
            && drained.FramesWithMultipleFixedSteps == 0 && drained.FramesWithoutFixedStep == 0,
            "Drain must reset the interval aggregates.");

        window.NoteFixedStep();
        window.NoteFixedStep();
        FrameStepSnapshot partial = window.Drain();
        Check(partial.PendingFixedSteps == 2 && partial.FramesObserved == 0 && partial.FixedStepsTotal == 0,
            "Steps of an unfinished frame are reported as pending, never as a completed frame.");
        window.NoteFrame();
        FrameStepSnapshot carried = window.Drain();
        Check(carried.FramesObserved == 1 && carried.FixedStepsTotal == 2 && carried.MaxFixedStepsPerFrame == 2
            && carried.PendingFixedSteps == 0,
            "Carried steps are counted once, in the interval whose frame completed them.");

        window.NoteFixedStep();
        window.NoteFrame();
        window.NoteFixedStep();
        window.Reset();
        FrameStepSnapshot afterReset = window.Drain();
        Check(afterReset.FramesObserved == 0 && afterReset.FixedStepsTotal == 0 && afterReset.PendingFixedSteps == 0,
            "Reset at capture start must drop pre-capture frames and any partial frame.");
    }
}
