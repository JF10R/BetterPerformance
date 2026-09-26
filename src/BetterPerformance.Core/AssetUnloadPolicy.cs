using System;

namespace BetterPerformance.Core
{
    public enum AssetUnloadDecision
    {
        // Native would only log "Skipping unloading unused assets": let it.
        NativeSkips,
        // Unload now through the native call.
        RunNative,
        // Past the deferral cap: unload now through the native call.
        RunCapped,
        // In play: skip the native call and wait for a calmer moment.
        Defer,
    }

    // Decides what the native hourly Game.CollectResourcesCheckPeriodic call may do. Pure, so
    // the deferral rules are testable without a game; docs/asset-unload-deferral.md.
    public static class AssetUnloadPolicy
    {
        // The native periodic check unloads once the last unload is this old.
        public const double NativePeriodSeconds = 3599;

        // secondsSincePlayStarted: on a server, how long peers have been connected without a
        // break (infinity when unknown, and on a client). The cap counts from the later of the last
        // unload and that start: an unload done on an empty server must not bring it forward.
        public static AssetUnloadDecision Periodic(double secondsSinceLastUnload, double secondsSincePlayStarted,
            double maxDeferSeconds, bool dedicatedServer, int peers)
        {
            if (maxDeferSeconds < NativePeriodSeconds) throw new ArgumentOutOfRangeException(nameof(maxDeferSeconds));
            if (!(secondsSinceLastUnload > NativePeriodSeconds)) return AssetUnloadDecision.NativeSkips;
            // An empty dedicated server has nobody to stall. A client always defers: its native
            // sleep, respawn and idle-pause checks unload at the next calm moment.
            if (dedicatedServer && peers == 0) return AssetUnloadDecision.RunNative;
            double deferred = secondsSinceLastUnload;
            if (secondsSincePlayStarted >= 0 && secondsSincePlayStarted < deferred) deferred = secondsSincePlayStarted;
            if (deferred >= maxDeferSeconds) return AssetUnloadDecision.RunCapped;
            return AssetUnloadDecision.Defer;
        }

        // A distant teleport keeps the loading screen up after the move (2 s) until the area is
        // ready: the one hidden moment a client meets often. The native 1200 s check then decides.
        public const double TeleportMoveSeconds = 2;

        public static bool OfferDuringTeleport(bool distant, double teleportSeconds, bool areaReady, bool offered) =>
            distant && !offered && areaReady && teleportSeconds > TeleportMoveSeconds;

        // A deferred server unload runs once the server is empty, unless an unload already happened.
        public static bool RunDeferredOnServer(bool pending, double secondsSinceLastUnload, int peers) =>
            pending && peers == 0 && secondsSinceLastUnload > NativePeriodSeconds;
    }
}
