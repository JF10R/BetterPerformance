using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using HarmonyLib;

namespace BetterPerformance
{
    // Harmony.UnpatchSelf walks every patched method in the process and reads each one's body first
    // (PatchFunctions.UnpatchConditional -> HasMethodBody); the first body that cannot load, even a
    // method another owner patched, aborts the walk and leaves this instance's later patches in
    // place. This removes only the methods the instance owns, one at a time, never reading a body
    // it does not unpatch, so one failing method stays one failure.
    internal static class PatchRemoval
    {
        // Returns how many owned methods still carry a patch of this instance; first is the first failure.
        internal static int UnpatchOwned(Harmony harmony, out Exception? first)
        {
            first = null;
            List<MethodBase> patched;
            // A snapshot: unpatching rewrites the registry this enumerates, as UnpatchConditional does.
            try { patched = Harmony.GetAllPatchedMethods().ToList(); }
            catch (Exception exception) { first = exception; return 1; }
            int failed = 0;
            foreach (MethodBase method in patched)
            {
                try
                {
                    if (!Owned(method, harmony.Id)) continue;
                    harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
                    if (Owned(method, harmony.Id))
                        throw new InvalidOperationException(method.DeclaringType?.FullName + "." + method.Name + " kept a patch of " + harmony.Id);
                }
                catch (Exception exception)
                {
                    failed++;
                    first ??= exception;
                }
            }
            return failed;
        }

        // Same removal; once every owned method was tried, rethrows the first failure with its own type.
        internal static void UnpatchOwned(Harmony harmony)
        {
            if (UnpatchOwned(harmony, out Exception? first) > 0) ExceptionDispatchInfo.Capture(first!).Throw();
        }

        // Reads the patch registry only; GetPatchInfo never touches a method body.
        private static bool Owned(MethodBase method, string id) => Harmony.GetPatchInfo(method)?.Owners.Contains(id) == true;
    }
}
