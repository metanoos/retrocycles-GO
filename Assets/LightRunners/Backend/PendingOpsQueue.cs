using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LightRunners.Backend
{
    /// <summary>
    /// Offline write queue (spec §21): <c>record_run</c> and trail-finalize payloads that
    /// failed after retries are persisted to <c>persistentDataPath/pending_ops.json</c> and
    /// flushed on the next successful connectivity (app launch or next run end). Auto-save
    /// batches are deliberately NOT queued — they're lossy by design.
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

        private static string FilePath => Path.Combine(Application.persistentDataPath, "pending_ops.json");

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
        /// Attempt every queued op via <paramref name="supabase"/>. Ops that fail again stay
        /// queued. Fire-and-forget; safe to call whenever connectivity may have returned.
        /// </summary>
        public static void Flush(SupabaseManager supabase)
        {
            if (supabase == null || !supabase.IsConfigured) return;
            OpList list;
            try { list = Load(); } catch { return; }
            if (list.ops.Count == 0) return;

            var remaining = new List<Op>(list.ops);
            foreach (var op in list.ops)
            {
                var captured = op;
                supabase.Rpc(op.fn, op.payload,
                    onSuccess: _ =>
                    {
                        remaining.Remove(captured);
                        try { Save(new OpList { ops = remaining }); }
                        catch { /* next flush retries */ }
                    },
                    onError: _ => { /* stays queued */ });
            }
        }

        private static OpList Load()
        {
            if (!File.Exists(FilePath)) return new OpList();
            var json = File.ReadAllText(FilePath);
            return JsonUtility.FromJson<OpList>(json) ?? new OpList();
        }

        private static void Save(OpList list) => File.WriteAllText(FilePath, JsonUtility.ToJson(list));
    }
}
