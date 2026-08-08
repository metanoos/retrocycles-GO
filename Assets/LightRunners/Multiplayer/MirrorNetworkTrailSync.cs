using UnityEngine;
using Mirror;
using LightRunners.Core;
using LightRunners.Trail;

namespace LightRunners.Multiplayer
{
    /// <summary>
    /// Mirror-based batched trail replication (spec §8.2). Free replacement for
    /// the Fusion NetworkTrailSync. Sits next to <see cref="MirrorNetworkPlayer"/>
    /// on the same GameObject.
    ///
    /// Wire precision (pitfall #16): the networked array holds float *offsets* from
    /// a per-batch double origin, scaled by 1e5 — never absolute lat/lon in a float.
    ///
    /// Authority sends from the **oldest unsent sequence** (pitfall #4). Clients
    /// apply each distinct batch exactly once; TrailManager's cursor merge makes
    /// overlaps idempotent.
    /// </summary>
    [RequireComponent(typeof(MirrorNetworkPlayer))]
    public class MirrorNetworkTrailSync : NetworkBehaviour
    {
        public const int MaxBatchPoints = 16;

        [SyncVar] public int BatchStart;
        [SyncVar] public int BatchCount;
        [SyncVar] public int BatchSeq;
        [SyncVar] public double OriginLat;
        [SyncVar] public double OriginLon;

        // Mirror SyncLists for the batch float array (SyncVar can't hold arrays).
        // Cleared and refilled each batch tick. We use a struct-free flat SyncList<float>.
        public class BatchList : SyncList<float> { }
        public readonly BatchList Batch = new BatchList();

        private MirrorNetworkPlayer _player;
        private int _nextSendSequence;
        private int _lastAppliedSeq;

        private void Awake()
        {
            _player = GetComponent<MirrorNetworkPlayer>();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            _nextSendSequence = 0;
            _lastAppliedSeq = 0;
            Batch.Callback += OnBatchChanged;
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            Batch.Callback -= OnBatchChanged;
        }

        private void OnBatchChanged(SyncList<float>.Operation op, int itemIndex, float oldItem, float newItem)
        {
            PullRemoteTrail();
        }

        private void Update()
        {
            if (isOwned) // Mirror: authority = owned locally
                PushLocalTrail();
            else
                PullRemoteTrail();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Authority → wire
        // ─────────────────────────────────────────────────────────────────────

        private void PushLocalTrail()
        {
            if (!TrailManager.HasInstance) return;
            var trail = TrailManager.Instance.LocalTrail;
            if (trail == null) return;
            if (trail.HighestAppliedSequence < _nextSendSequence) return;

            int batchSize = Mathf.Min(GameConfig.Active.trailSyncBatchSize, MaxBatchPoints);
            TrailSnapshot snap = trail.TakeSnapshot(_nextSendSequence, batchSize);
            if (snap.Count == 0) return;

            OriginLat = snap.originLat;
            OriginLon = snap.originLon;
            BatchStart = snap.startIndex;
            BatchCount = snap.Count;

            // Mirror SyncList: replace contents atomically.
            // Clear + add individual would fire N callbacks; use OnDeserialize guard
            // by setting a flag the callback checks. Simpler: just refill and bump
            // BatchSeq so clients know to re-read.
            Batch.Clear();
            for (int i = 0; i < snap.Count * 4; i++)
                Batch.Add(snap.points[i]);

            BatchSeq++;
            _nextSendSequence = snap.startIndex + snap.Count;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Wire → proxy
        // ─────────────────────────────────────────────────────────────────────

        private void PullRemoteTrail()
        {
            if (BatchSeq == _lastAppliedSeq || BatchCount <= 0) return;
            _lastAppliedSeq = BatchSeq;
            if (!TrailManager.HasInstance || _player == null) return;

            int floats = Mathf.Min(BatchCount, MaxBatchPoints) * 4;
            var data = new float[floats];
            for (int i = 0; i < floats && i < Batch.Count; i++)
                data[i] = Batch[i];

            var snap = new TrailSnapshot(_player.PlayerId, BatchStart, OriginLat, OriginLon, data);
            TrailManager.Instance.UpdateRemoteTrail(snap.ownerId, snap);
        }
    }
}
