using System;
using System.Collections.Generic;
using System.Reflection;
using BetterPerformance.Core;
using HarmonyLib;

namespace BetterPerformance
{
    // Shared by MinimapTextureCache and BiomePointCache. Both used to gate their cache key
    // on this plugin's own version, as a stand-in for "a patch on the generation path might
    // have changed"; every release then invalidated every entry, so a verified hit never
    // survived to serve. This describes every Harmony patch (prefix/postfix/transpiler/
    // finalizer, any owner) attached to a closure method instead, so the key moves only when
    // a patch actually could change the cached output.
    //
    // IlFingerprint.Compute is token-independent (see its header comment): a raw IL hash of
    // a patch method would still change on an unrelated rebuild of the assembly that defines
    // it, which is exactly the failure mode this replaces.
    internal static class PatchFingerprint
    {
        // fallback is set when a patch method could not be fingerprinted (e.g. a dynamic
        // method Harmony cannot describe); that one patch falls back to fallbackVersion, and
        // callers must surface the flag so a stuck fallback stays visible rather than silently
        // re-gating the whole key on a version bump.
        internal static string Describe(MethodBase method, string fallbackVersion, out bool fallback)
        {
            fallback = false;
            var descriptors = new List<PatchDescriptor>();
            var info = Harmony.GetPatchInfo(method);
            if (info != null)
            {
                Collect(info.Prefixes, "prefix", fallbackVersion, descriptors, ref fallback);
                Collect(info.Postfixes, "postfix", fallbackVersion, descriptors, ref fallback);
                Collect(info.Transpilers, "transpiler", fallbackVersion, descriptors, ref fallback);
                Collect(info.Finalizers, "finalizer", fallbackVersion, descriptors, ref fallback);
            }
            return CacheKeyMaterial.DescribePatches(descriptors);
        }

        private static void Collect(IEnumerable<Patch> patches, string kind, string fallbackVersion,
            List<PatchDescriptor> descriptors, ref bool fallback)
        {
            foreach (var patch in patches)
            {
                MethodInfo? patchMethod = patch.PatchMethod;
                string fingerprint;
                try
                {
                    if (patchMethod == null) throw new InvalidOperationException("Patch method unavailable.");
                    fingerprint = IlFingerprint.Compute(patchMethod);
                }
                catch (Exception) { fingerprint = "unfingerprintable:" + fallbackVersion; fallback = true; }
                descriptors.Add(new PatchDescriptor(patch.owner, kind, patch.priority,
                    patchMethod?.DeclaringType?.FullName ?? "<null>", patchMethod?.Name ?? "<null>", fingerprint));
            }
        }
    }
}
