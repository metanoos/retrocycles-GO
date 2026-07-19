using System;

namespace LightRunners.Trail
{
    // ─── DEPRECATED — Lightfield migration (active decision E, 2026-07-18) ────
    // RunScorer's 4-axis float score (distance/speed/beauty/proximity /100) has
    // been REPLACED by the integer Lumen tally on ILumenScoreboard (Core). This
    // file is retained ONLY as a compile-compatible stub: the ScoreBreakdown
    // struct shape is preserved so existing call sites compile during the
    // parallel-track migration. Calculate() now returns zeros.
    //
    // Track D (Gameplay/RunSummaryUI.cs) and Track E (Backend/PlayerRepository.cs
    // + Supabase schema) will replace these call sites with the Lumen model and
    // delete this file. Grep for `RunScorer` and `ScoreBreakdown` to find them.
    // The 16 original axis tests (Tests/RunScorerTests.cs) have been deleted.

    /// <summary>
    /// DEPRECATED shape kept for compile compatibility. All fields are zero from
    /// <see cref="RunScorer.Calculate"/>. Will be removed when Track D/E land.
    /// </summary>
    [Serializable]
    [Obsolete("Replaced by ILumenScoreboard (Core). Retained as a stub for the Lightfield migration; removed by Track D/E.")]
    public struct ScoreBreakdown
    {
        public int distance;   // /40  (always 0 from Calculate)
        public int speed;      // /20  (always 0)
        public int beauty;     // /30  (always 0)
        public int proximity;  // /10  (always 0)
        public int total;      //      (always 0)

        [Obsolete("Replaced by ILumenScoreboard.")]
        public int Max => 40 + 20 + 30 + 10;
    }

    /// <summary>
    /// DEPRECATED. Returns an all-zero breakdown. Real scoring now lives on
    /// <c>LightRunners.Core.ILumenScoreboard</c> (Lumens accrued live during a
    /// timed match — decisions E, F, O).
    /// </summary>
    [Obsolete("Replaced by ILumenScoreboard (Core). Retained as a stub for the Lightfield migration; removed by Track D/E.")]
    public static class RunScorer
    {
        [Obsolete("Replaced by ILumenScoreboard (Core). Returns zeros.")]
        public static ScoreBreakdown Calculate(TrailData trail, double durationSeconds, int otherPlayersNearby)
        {
            // TODO(lumen-scoreboard): Track D replaces this call site in
            // Gameplay/RunSummaryUI.cs; Track E removes the persistence path in
            // Backend/PlayerRepository.cs and Supabase/schema.sql. Once both
            // land, delete this file.
            return default;
        }
    }
}