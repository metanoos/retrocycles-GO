using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using LightRunners.Backend;

namespace LightRunners.Tests.Backend
{
    /// <summary>
    /// Static-analysis guards over <c>Supabase/schema.sql</c> for the
    /// lumen-scoreboard migration (Track E). We can't run Postgres in this
    /// environment, so these tests parse the SQL TEXT and assert the migration's
    /// content: (a) the <c>matches</c> + <c>match_players</c> tables exist with
    /// the expected columns; (b) the <c>record_run</c> RPC no longer references
    /// the deprecated <c>score_*</c> params; (c) the new RPCs
    /// (<c>create_match</c>, <c>record_match_result</c>, <c>finalize_match</c>)
    /// exist; (d) the DROP COLUMN statements are present AND idempotent
    /// (<c>IF EXISTS</c>).
    ///
    /// This is a CONTENT guard, not a live-DB test. A live run of schema.sql
    /// against a real Supabase project is a HUMAN CHECKPOINT — mirror the
    /// existing "not verified on this machine" note in the repo README. If you
    /// change the migration, update both this file and re-run it by hand.
    /// </summary>
    [TestFixture]
    public class MatchSchemaTests
    {
        private static readonly string SchemaPath = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "Supabase", "schema.sql"));
        private static readonly string AuthServicePath = Path.Combine(
            Application.dataPath, "LightRunners", "Identity", "SupabaseAuthService.cs");
        private static readonly string MatchManagerPath = Path.Combine(
            Application.dataPath, "LightRunners", "Gameplay", "MatchManager.cs");

        private string _sql;          // raw text — exact-substring assertions
        private string _sqlNorm;      // whitespace-collapsed — alignment-tolerant assertions
        private string _authSource;
        private string _matchManagerSource;

        [OneTimeSetUp]
        public void LoadSchema()
        {
            Assert.IsTrue(File.Exists(SchemaPath),
                $"schema.sql not found at {SchemaPath}. The migration target is checked in at the repo root.");
            _sql = File.ReadAllText(SchemaPath);
            Assert.IsFalse(string.IsNullOrEmpty(_sql), "schema.sql is empty.");
            // Collapse every run of whitespace (spaces/tabs/newlines) to a single space so
            // assertions don't break on the file's column-alignment spacing (e.g. the
            // `alter table ... enable row level security` block uses padded names).
            _sqlNorm = System.Text.RegularExpressions.Regex.Replace(_sql, @"\s+", " ");
            _authSource = File.ReadAllText(AuthServicePath);
            _matchManagerSource = File.ReadAllText(MatchManagerPath);
        }

        // ─── (a) matches table ────────────────────────────────────────────────

        [Test]
        public void Matches_Table_Exists_With_Expected_Columns()
        {
            Assert.That(_sql, Does.Contain("create table if not exists public.matches"),
                "matches CREATE TABLE missing — migration must add the matches table idempotently.");

            // Required columns per Track E spec.
            Assert.That(_sql, Does.Contain("id").And.Contains("uuid primary key"),
                "matches.id uuid primary key missing.");
            Assert.That(_sql, Does.Contain("room_id"),
                "matches.room_id missing.");
            Assert.That(_sql, Does.Contain("started_at"),
                "matches.started_at missing.");
            Assert.That(_sql, Does.Contain("ended_at"),
                "matches.ended_at missing.");
            Assert.That(_sql, Does.Contain("duration_seconds"),
                "matches.duration_seconds missing.");
            Assert.That(_sql, Does.Contain("winner_player_id"),
                "matches.winner_player_id missing.");
        }

        [Test]
        public void Matches_Table_UsesGenRandomUuid_Default()
        {
            // The matches.id default must be gen_random_uuid() so the host
            // doesn't have to mint an id client-side.
            Assert.That(_sql, Does.Contain("gen_random_uuid()"),
                "matches.id should default to gen_random_uuid().");
        }

        // ─── (a) match_players table ──────────────────────────────────────────

        [Test]
        public void MatchPlayers_Table_Exists_With_Expected_Columns()
        {
            Assert.That(_sql, Does.Contain("create table if not exists public.match_players"),
                "match_players CREATE TABLE missing — migration must add the table idempotently.");

            Assert.That(_sql, Does.Contain("match_id").And.Contains("references public.matches"),
                "match_players.match_id must reference matches(id).");
            Assert.That(_sql, Does.Contain("on delete cascade"),
                "match_players.match_id must ON DELETE CASCADE with matches.");
            Assert.That(_sql, Does.Contain("player_id"),
                "match_players.player_id missing.");
            Assert.That(_sql, Does.Contain("lumens"),
                "match_players.lumens missing.");
            Assert.That(_sql, Does.Contain("finish_rank"),
                "match_players.finish_rank missing.");
            Assert.That(_sql, Does.Contain("role"),
                "match_players.role missing.");
        }

        [Test]
        public void MatchPlayers_Role_Check_Constraint_Matches_PlayerRole_Enum()
        {
            // role must be CHECK-constrained to the three PlayerRole enum values
            // (decision Q/R: runner | host | referee).
            Assert.That(_sql, Does.Contain("check (role in ('runner','host','referee'))"),
                "match_players.role must be CHECK-constrained to runner/host/referee.");
        }

        [Test]
        public void MatchPlayers_Has_Composite_Primary_Key()
        {
            // PK is (match_id, player_id) so a player can't double-up in a match.
            Assert.That(_sql, Does.Contain("primary key (match_id, player_id)"),
                "match_players composite primary key (match_id, player_id) missing.");
        }

        // ─── (b) record_run no longer references score_* ───────────────────────

        [Test]
        public void RecordRun_RPC_Takes_Lumens_Not_Score_Params()
        {
            Assert.That(_sql, Does.Contain("create or replace function public.record_run"),
                "record_run RPC definition missing.");

            // New Lumen param.
            Assert.That(_sql, Does.Contain("p_lumens int"),
                "record_run must take p_lumens int (decision E).");

            // The new signature must NOT carry the deprecated score_* params.
            // (We check the record_run parameter block specifically by looking
            // for score_ prefixed params, which only existed in the old signature.)
            Assert.That(_sql, Does.Not.Contains("score_total int"),
                "record_run must not take score_total — RunScorer is dropped.");
            Assert.That(_sql, Does.Not.Contains("score_distance int"),
                "record_run must not take score_distance.");
            Assert.That(_sql, Does.Not.Contains("score_speed int"),
                "record_run must not take score_speed.");
            Assert.That(_sql, Does.Not.Contains("score_beauty int"),
                "record_run must not take score_beauty.");
            Assert.That(_sql, Does.Not.Contains("score_proximity int"),
                "record_run must not take score_proximity.");
        }

        // ─── (c) new match RPCs exist ─────────────────────────────────────────

        [Test]
        public void CreateMatch_RPC_Exists_With_Expected_Signature()
        {
            Assert.That(_sql, Does.Contain("create or replace function public.create_match"),
                "create_match RPC missing.");
            Assert.That(_sql, Does.Contain("p_room_id text"),
                "create_match must take p_room_id text.");
            Assert.That(_sql, Does.Contain("p_host_player_id text"),
                "create_match must take p_host_player_id text.");
            Assert.That(_sql, Does.Contain("p_match_id uuid default gen_random_uuid()"),
                "create_match must accept the runtime replay UUID.");
            Assert.That(_sql, Does.Contain("returns uuid"),
                "create_match must return uuid.");
        }

        [Test]
        public void CreateMatch_RPC_IsRetrySafeForStableMatchId()
        {
            Assert.That(_sqlNorm, Does.Contain("on conflict (id) do nothing"),
                "a lost create response must be safely retryable with the replay UUID");
            Assert.That(_sqlNorm, Does.Contain("m.room_id is not distinct from p_room_id"),
                "idempotent create must reject reuse of a UUID for another room");
            Assert.That(_sqlNorm, Does.Contain("h.player_id = v_uid::text and h.role = 'host'"),
                "idempotent create must return only to the authenticated original host");
            Assert.That(_sql, Does.Contain("match_id_conflict"));
        }

        [Test]
        public void RecordMatchResult_RPC_Exists_With_Expected_Signature()
        {
            Assert.That(_sql, Does.Contain("create or replace function public.record_match_result"),
                "record_match_result RPC missing.");
            Assert.That(_sql, Does.Contain("p_match_id uuid"),
                "record_match_result must take p_match_id uuid.");
            Assert.That(_sql, Does.Contain("p_player_id text"),
                "record_match_result must take p_player_id text.");
            Assert.That(_sql, Does.Contain("p_lumens int"),
                "record_match_result must take p_lumens int.");
            Assert.That(_sql, Does.Contain("p_finish_rank int"),
                "record_match_result must take p_finish_rank int.");
            Assert.That(_sql, Does.Contain("p_role text"),
                "record_match_result must take p_role text.");
        }

        [Test]
        public void FinalizeMatch_RPC_Exists_With_Expected_Signature()
        {
            Assert.That(_sql, Does.Contain("create or replace function public.finalize_match"),
                "finalize_match RPC missing.");
            Assert.That(_sql, Does.Contain("p_winner_player_id text"),
                "finalize_match must take p_winner_player_id text.");
            Assert.That(_sql, Does.Contain("p_duration_seconds int"),
                "finalize_match must take p_duration_seconds int.");
        }

        [Test]
        public void AtomicFinalizeWithResults_RPC_Exists()
        {
            Assert.That(_sql, Does.Contain("create or replace function public.finalize_match_with_results"));
            Assert.That(_sqlNorm, Does.Contain(
                "finalize_match_with_results( p_match_id uuid, p_room_id text, p_host_player_id text, p_results jsonb"));
            Assert.That(_sql, Does.Contain("p_results jsonb"));
            Assert.That(_sql, Does.Contain("p_results is null"));
            Assert.That(_sql, Does.Contain("jsonb_array_elements(p_results)"));
            Assert.That(_sql, Does.Contain("perform public.create_match"),
                "the sole persistence transaction must create and close the match together");
            Assert.That(_sql, Does.Contain("perform public.record_match_result"));
            Assert.That(_sql, Does.Contain("perform public.finalize_match"));
            Assert.That(_sqlNorm, Does.Contain("ended_at = coalesce(ended_at, now())"),
                "replaying a durable finalization must preserve its original end timestamp");
        }

        [Test]
        public void RuntimePersistsOnlyAtMatchEnd_AndFlushesAfterAuthRestore()
        {
            Assert.That(_matchManagerSource, Does.Not.Contain(".CreateMatch("),
                "mid-match creation can orphan an open row if the app is terminated");
            Assert.That(_matchManagerSource, Does.Contain("FinalizeMatchWithResults("));
            Assert.That(_authSource, Does.Contain("PendingOpsQueue.Flush(_supabase)"),
                "queued SECURITY DEFINER writes must replay after the restored JWT is installed");
        }

        [Test]
        public void PendingOutbox_RemoveReloadsAndPreservesNewlyEnqueuedOperation()
        {
            string path = Path.Combine(Path.GetTempPath(), $"light-runners-pending-{Guid.NewGuid():N}.json");
            PendingOpsQueue.FilePathOverrideForTests = path;
            try
            {
                PendingOpsQueue.EnqueueUnique("record_run", "{\"id\":\"a\"}");
                // Represents the snapshot an async flush took before the next match ended.
                Assert.AreEqual(1, PendingOpsQueue.SnapshotForTests().Count);
                PendingOpsQueue.EnqueueUnique("finalize_match_with_results", "{\"id\":\"b\"}");

                // A's later success must reload the file and remove only A, never save its stale
                // one-item snapshot over the newly enqueued finalization B.
                PendingOpsQueue.Remove("record_run", "{\"id\":\"a\"}");
                var remaining = PendingOpsQueue.SnapshotForTests();
                Assert.AreEqual(1, remaining.Count);
                Assert.AreEqual("finalize_match_with_results", remaining[0].fn);

                PendingOpsQueue.EnqueueUnique("finalize_match_with_results", "{\"id\":\"b\"}");
                Assert.AreEqual(1, PendingOpsQueue.SnapshotForTests().Count,
                    "stable match outbox entries must be deduplicated");
            }
            finally
            {
                PendingOpsQueue.ResetForTests();
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void Match_RPCs_Are_Security_Definer()
        {
            // All three match RPCs must be SECURITY DEFINER (mirrors the lobby
            // RPC pattern) so they can write to RLS-locked tables.
            int definerCount = CountOccurrences(_sql, "security definer set search_path = public");
            Assert.GreaterOrEqual(definerCount, 4,
                "all four match mutation RPCs must be SECURITY DEFINER.");
        }

        // ─── (d) DROP COLUMN statements are present + idempotent ──────────────

        [Test]
        public void Score_Columns_Dropped_Idempotently()
        {
            // Each of the five score_* columns must be dropped with IF EXISTS
            // so re-applying schema.sql is a no-op.
            string[] scoreCols = { "score_total", "score_distance", "score_speed", "score_beauty", "score_proximity" };
            foreach (var col in scoreCols)
            {
                Assert.That(_sql, Does.Contain($"drop column if exists {col}"),
                    $"{col} must be dropped with IF EXISTS for migration idempotency.");
            }
        }

        [Test]
        public void RunHistory_Has_Lumens_Column_Added_Idempotently()
        {
            Assert.That(_sql, Does.Contain("add column if not exists lumens int not null default 0"),
                "run_history.lumens must be added with IF NOT EXISTS (idempotent).");
        }

        // ─── Extras: RLS, host-authority, retention ───────────────────────────

        [Test]
        public void RLS_Enabled_On_Match_Tables()
        {
            // Uses the whitespace-normalized text — the schema pads the ALTER TABLE
            // names for column alignment (see the RLS block), so a literal substring
            // with single spaces wouldn't match the raw file.
            Assert.That(_sqlNorm, Does.Contain("alter table public.matches enable row level security"),
                "RLS must be enabled on matches.");
            Assert.That(_sqlNorm, Does.Contain("alter table public.match_players enable row level security"),
                "RLS must be enabled on match_players.");
        }

        [Test]
        public void Matches_Retention_Sweep_Exists()
        {
            // The matches table needs the same 30-day retention sweep as trails.
            Assert.That(_sql, Does.Contain("create or replace function public.sweep_old_matches"),
                "sweep_old_matches() RPC missing.");
            Assert.That(_sql, Does.Contain("interval '30 days'"),
                "sweep_old_matches must use the 30-day retention window (spec §23).");
        }

        [Test]
        public void MatchWrites_AreBoundToAuthenticatedHost()
        {
            Assert.That(_sql, Does.Contain("p_host_player_id is distinct from v_uid::text"),
                "create_match must not let callers designate another user as host.");
            Assert.That(_sql, Does.Contain("not_host"),
                "match writes must raise 'not_host' for unauthorized callers.");
            Assert.That(_sqlNorm, Does.Contain("if not coalesce(v_caller_is_host, false) then raise exception 'not_host'"),
                "record_match_result must be host-only, including writes to the caller's own row.");
        }

        [Test]
        public void RecordMatchResult_RejectsInvalidLumensAndRank()
        {
            Assert.That(_sql, Does.Contain("p_lumens is null or p_lumens < 0"));
            Assert.That(_sql, Does.Contain("p_finish_rank is null or p_finish_rank < 1"));
        }

        [Test]
        public void Migration_Has_Clear_Section_Headers()
        {
            // The repo has no migration framework; reviewers rely on the
            // "-- Migration:" headers to find the Track E work.
            Assert.That(_sql, Does.Contain("Migration: lumen-scoreboard"),
                "The Track E migration must be marked with '-- Migration: lumen-scoreboard' headers.");
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                count++;
                i += needle.Length;
            }
            return count;
        }
    }
}
