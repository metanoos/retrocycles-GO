using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LightRunners.Backend
{
    /// <summary>
    /// Offline write queue (spec §21): <c>record_run</c>, trail-finalize, and atomic
    /// match-finalize payloads that failed after retries are persisted to
    /// <c>persistentDataPath/pending_ops.json</c> and flushed on the next successful
    /// connectivity (app launch or next run end). Auto-save batches are deliberately NOT
    /// queued — they're lossy by design.
    /// </summary>
    public static class PendingOpsQueue
    {
        [Serializable]
        public class Op
        {
            public string fn;      // RPC function name
            public string payload; // JSON body
        }

        [Serializable]
        private class OpList
        {
            public List<Op> ops = new List<Op>();
        }

#if UNITY_INCLUDE_TESTS
        internal static string FilePathOverrideForTests;
#endif

        private static string FilePath
        {
            get
            {
#if UNITY_INCLUDE_TESTS
                if (!string.IsNullOrEmpty(FilePathOverrideForTests))
                    return FilePathOverrideForTests;
#endif
                return Path.Combine(Application.persistentDataPath, "pending_ops.json");
            }
        }
        private static bool _flushInProgress;

        public static void Enqueue(string fn, string payload)
        {
            try
            {
                var list = Load();
                list.ops.Add(new Op { fn = fn, payload = payload });
                // Bound the queue: a device that's offline for weeks shouldn't hoard megabytes.
                while (list.ops.Count > 100) list.ops.RemoveAt(0);
                Save(list);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PendingOpsQueue] enqueue failed: {e.Message}");
            }
        }

        /// <summary>
        /// Persist an idempotent operation once. Stable match UUID payloads use this before
        /// dispatch so an app kill or lost response cannot orphan their server-side lifecycle.
        /// </summary>
        public static void EnqueueUnique(string fn, string payload)
        {
            try
            {
                var list = Load();
                foreach (var op in list.ops)
                    if (SameOperation(op, fn, payload)) return;
                list.ops.Add(new Op { fn = fn, payload = payload });
                while (list.ops.Count > 100) list.ops.RemoveAt(0);
                Save(list);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PendingOpsQueue] enqueue failed: {e.Message}");
            }
        }

        /// <summary>Remove the first exact operation after confirmed server success.</summary>
        public static void Remove(string fn, string payload)
        {
            try
            {
                var list = Load();
                for (int i = 0; i < list.ops.Count; i++)
                {
                    if (!SameOperation(list.ops[i], fn, payload)) continue;
                    list.ops.RemoveAt(i);
                    Save(list);
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PendingOpsQueue] remove failed: {e.Message}");
            }
        }

        /// <summary>
        /// Attempt every queued op via <paramref name="supabase"/> in insertion order. Ops that
        /// fail stay queued while later independent ops still get one attempt. Serialized
        /// dispatch plus reload-on-remove prevents an older async callback from overwriting an
        /// operation enqueued while the flush was in flight. Fire-and-forget; safe to call
        /// whenever authenticated connectivity may have returned.
        /// </summary>
        public static void Flush(SupabaseManager supabase)
        {
            if (_flushInProgress || supabase == null || !supabase.IsConfigured) return;
            OpList list;
            try { list = Load(); } catch { return; }
            if (list.ops.Count == 0) return;

            _flushInProgress = true;
            FlushNext(supabase, new List<Op>(list.ops), 0);
        }

        private static void FlushNext(SupabaseManager supabase, List<Op> batch, int index)
        {
            if (index >= batch.Count)
            {
                _flushInProgress = false;
                return;
            }

            Op op = batch[index];
            try
            {
                supabase.Rpc(op.fn, op.payload,
                    onSuccess: _ =>
                    {
                        Remove(op.fn, op.payload);
                        FlushNext(supabase, batch, index + 1);
                    },
                    onError: _ => FlushNext(supabase, batch, index + 1));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PendingOpsQueue] flush dispatch failed: {e.Message}");
                FlushNext(supabase, batch, index + 1);
            }
        }

        private static bool SameOperation(Op op, string fn, string payload)
            => op != null && string.Equals(op.fn, fn, StringComparison.Ordinal)
                && string.Equals(op.payload, payload, StringComparison.Ordinal);

#if UNITY_INCLUDE_TESTS
        internal static IReadOnlyList<Op> SnapshotForTests()
            => new List<Op>(Load().ops);

        internal static void ResetForTests()
        {
            _flushInProgress = false;
            FilePathOverrideForTests = null;
        }
#endif

        private static OpList Load()
        {
            if (!File.Exists(FilePath)) return new OpList();
            var json = File.ReadAllText(FilePath);
            return JsonUtility.FromJson<OpList>(json) ?? new OpList();
        }

        private static void Save(OpList list) => File.WriteAllText(FilePath, JsonUtility.ToJson(list));
    }
}
