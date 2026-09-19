using System;
using System.Collections.Generic;

namespace BetterPerformance.Core
{
    // Per-interval accounting of the send-window decisions. Counters are additive and
    // only Drain resets them, so an exporter drains at its own cadence.
    public struct SendWindowSummary
    {
        public long Updates, Backoffs, Recoveries, UnknownRate, PeersOverCapacity;
        // Smallest and largest window returned during the interval; 0/0 when no peer was updated.
        public int WindowMin, WindowMax;
        // Peers tracked at the moment of the drain; not an interval counter.
        public int Peers;
    }

    // The per-peer send window in bytes that replaces the fixed 10,240 B gate in
    // ZDOMan.SendZDOs. Single-threaded and clock-injected: the caller supplies Steam's
    // rate and ping estimates, so the policy is testable without a game or a socket.
    //
    // The window tracks bandwidth-delay product with a margin. It halves on sustained
    // backlog and returns to target by bounded steps, never by a jump, so a peer that
    // just recovered cannot be handed a burst it has not proven it can absorb.
    public sealed class SendWindowController
    {
        // Peers beyond this are served the default window and counted; the dictionary is bounded.
        public const int MaxPeers = 64;
        // Backlog must be over half the window on two updates this far apart before a back-off.
        public const double BackoffSampleSeconds = 0.2;
        // Growth is suppressed for this long after a back-off.
        public const double HoldSeconds = 2.0;

        private sealed class State
        {
            public int Window;
            public double LastSeconds, HoldUntilSeconds, OverSinceSeconds;
            public bool Initialized, HasOverSince, Recovering;
        }

        private readonly Dictionary<long, State> states = new Dictionary<long, State>();
        private readonly int minBytes, maxBytes, defaultBytes;
        private readonly double tickSeconds, margin;
        private long updates, backoffs, recoveries, unknownRate, peersOverCapacity;
        private int windowMin, windowMax;
        private bool sawWindow;

        public SendWindowController(int minBytes = 16384, int maxBytes = 262144, double tickSeconds = 0.05,
            double margin = 1.5, int defaultBytes = 32768)
        {
            if (minBytes < 1) throw new ArgumentOutOfRangeException(nameof(minBytes));
            if (maxBytes < minBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            if (double.IsNaN(tickSeconds) || double.IsInfinity(tickSeconds) || tickSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(tickSeconds));
            if (double.IsNaN(margin) || double.IsInfinity(margin) || margin <= 0)
                throw new ArgumentOutOfRangeException(nameof(margin));
            this.minBytes = minBytes;
            this.maxBytes = maxBytes;
            this.tickSeconds = tickSeconds;
            this.margin = margin;
            // A configured default outside the window range is clamped rather than fatal:
            // a bad config value must not take the game down at connection time.
            this.defaultBytes = defaultBytes < minBytes ? minBytes : (defaultBytes > maxBytes ? maxBytes : defaultBytes);
        }

        public int Peers => states.Count;

        // Last computed window for the peer, or the default for one never updated.
        public int Window(long peer) => states.TryGetValue(peer, out State? state) && state.Initialized ? state.Window : defaultBytes;

        public void Forget(long peer) => states.Remove(peer);

        // rateBytesPerSecond <= 0 means Steam has no estimate yet; pingMs and pendingBytes
        // below zero are read as zero, and a non-finite or backwards nowSeconds as "same time".
        public int Update(long peer, int rateBytesPerSecond, int pingMs, long pendingBytes, double nowSeconds)
        {
            if (pingMs < 0) pingMs = 0;
            if (pendingBytes < 0) pendingBytes = 0;
            bool finite = !double.IsNaN(nowSeconds) && !double.IsInfinity(nowSeconds);

            if (!states.TryGetValue(peer, out State? state))
            {
                if (states.Count >= MaxPeers) { peersOverCapacity++; return defaultBytes; }
                state = new State { Window = defaultBytes, LastSeconds = finite ? nowSeconds : 0 };
                states.Add(peer, state);
            }

            double now = finite && nowSeconds >= state.LastSeconds ? nowSeconds : state.LastSeconds;
            int target = Target(rateBytesPerSecond, pingMs);

            // Backlog is measured against the window in force before this update.
            bool over = pendingBytes * 2 > state.Window;
            bool backoff = false;
            if (over)
            {
                if (state.HasOverSince) backoff = now - state.OverSinceSeconds >= BackoffSampleSeconds;
                else { state.HasOverSince = true; state.OverSinceSeconds = now; }
            }
            else state.HasOverSince = false;

            if (backoff)
            {
                state.Window = Clamp(state.Window / 2);
                state.HasOverSince = false;
                state.HoldUntilSeconds = now + HoldSeconds;
                state.Recovering = true;
                state.Initialized = true;
                backoffs++;
            }
            else if (!state.Initialized)
            {
                // A peer's first window is the target outright; there is nothing to ramp from.
                state.Window = target;
                state.Initialized = true;
            }
            else
            {
                if (target < state.Window) state.Window = target;                       // down moves are immediate
                else if (target > state.Window && now >= state.HoldUntilSeconds)
                {
                    int step = state.Window + state.Window / 4;                         // at most +25 % per update
                    state.Window = step < target ? step : target;
                }
                if (state.Recovering && state.Window == target) { recoveries++; state.Recovering = false; }
            }

            state.Window = Clamp(state.Window);
            state.LastSeconds = now;
            updates++;
            if (!sawWindow) { windowMin = windowMax = state.Window; sawWindow = true; }
            else
            {
                if (state.Window < windowMin) windowMin = state.Window;
                if (state.Window > windowMax) windowMax = state.Window;
            }
            return state.Window;
        }

        public SendWindowSummary Drain()
        {
            var summary = new SendWindowSummary
            {
                Updates = updates,
                Backoffs = backoffs,
                Recoveries = recoveries,
                UnknownRate = unknownRate,
                PeersOverCapacity = peersOverCapacity,
                WindowMin = sawWindow ? windowMin : 0,
                WindowMax = sawWindow ? windowMax : 0,
                Peers = states.Count,
            };
            updates = backoffs = recoveries = unknownRate = peersOverCapacity = 0;
            windowMin = windowMax = 0;
            sawWindow = false;
            return summary;
        }

        private int Target(int rateBytesPerSecond, int pingMs)
        {
            if (rateBytesPerSecond <= 0) { unknownRate++; return defaultBytes; }
            double bytes = rateBytesPerSecond * (pingMs / 1000.0 + tickSeconds) * margin;
            if (double.IsNaN(bytes)) return defaultBytes;
            if (bytes >= maxBytes) return maxBytes;
            if (bytes <= minBytes) return minBytes;
            return Clamp((int)Math.Round(bytes));
        }

        private int Clamp(int bytes) => bytes < minBytes ? minBytes : (bytes > maxBytes ? maxBytes : bytes);
    }
}
