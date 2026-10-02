using System;
using BetterPerformance.Core;

// The owner-grant expedite quota and dedupe window: a capture restart must not reopen them.
internal static class AdmissionWindowTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run()
    {
        var window = new AdmissionWindow<long>(1000, 4096);
        Check(window.Admit(1, 100, 2) == Admission.Admitted && window.Admit(2, 150, 2) == Admission.Admitted,
            "Two grants fit a quota of two.");
        Check(window.Admit(3, 200, 2) == Admission.Capacity, "A third grant in the same second is over quota.");
        // A capture stop and restart in the same second resets statistics only; the window object is untouched,
        // so neither a duplicate nor an extra admission gets through before the second ends.
        Check(window.Admit(1, 400, 5) == Admission.Duplicate, "A restart within the second cannot re-admit a grant already forced.");
        Check(window.Admit(4, 400, 2) == Admission.Capacity, "A restart within the second cannot add an admission over quota.");
        var dedupe = new AdmissionWindow<long>(1000, 4096);
        Check(dedupe.Admit(7, 10, 4) == Admission.Admitted && dedupe.Admit(7, 20, 4) == Admission.Duplicate,
            "The same grant twice in one second is a duplicate.");
        Check(dedupe.Admit(7, 1010, 4) == Admission.Admitted, "A new second reopens the window.");
        var bounded = new AdmissionWindow<long>(1000, 2);
        Check(bounded.Admit(1, 0, 10) == Admission.Admitted && bounded.Admit(2, 0, 10) == Admission.Admitted &&
            bounded.Admit(3, 0, 10) == Admission.Capacity, "The dedupe set is bounded.");
        Check(new AdmissionWindow<long>(1000, 4096).Admit(1, 0, 0) == Admission.Capacity, "A zero quota admits nothing.");
        bounded.Clear();
        Check(bounded.Count == 0 && bounded.Admit(1, 0, 10) == Admission.Admitted, "Clear starts a fresh window at zero.");
    }
}
