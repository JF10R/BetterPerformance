using System;
using BetterPerformance.Core;

internal static class PositionJumpPolicyTests
{
    public static void Run()
    {
        Check(PositionJumpPolicy.IsJump(64, 64) && PositionJumpPolicy.IsJump(3000, 64), "a portal is a jump");
        Check(!PositionJumpPolicy.IsJump(12, 64), "walking or riding for 2 s is not");
        Check(!PositionJumpPolicy.IsJump(double.NaN, 64) && !PositionJumpPolicy.IsJump(double.PositiveInfinity, 64), "an unreadable distance is not");
        Check(!PositionJumpPolicy.IsJump(500, 0), "no threshold, no jump");
        Check(PositionJumpPolicy.MaySend(10, double.NaN), "the first early send is allowed");
        Check(!PositionJumpPolicy.MaySend(10.1, 10) && PositionJumpPolicy.MaySend(10.25, 10), "at most one per 0.25 s");
        Check(PositionJumpPolicy.MaySend(5, 10), "a clock that went back never blocks");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
