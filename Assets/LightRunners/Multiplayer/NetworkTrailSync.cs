#if FUSION_WEAVER
using UnityEngine;
using Fusion;
using LightRunners.Core;
using LightRunners.Trail;

namespace LightRunners.Multiplayer
{
    /// <summary>
    /// Batched trail replication (spec §8.2) — the heart of multiplayer visibility. Sits next
    /// to <see cref="NetworkPlayer"/> on the same NetworkObject.
    ///
    /// Wire precision (pitfall #16): the networked array holds float *offsets* from a
    /// per-batch double origin (<see cref="OriginLat"/>/<see cref="OriginLon"/>), scaled by
    /// 1e5 — never absolute lat/lon in a float.
    ///
    /// Authority sends from the **oldest unsent sequence** (pitfall #4 — never the freshest
    /// tail, or proxies bridge gaps with phantom straight segments). Proxies apply each
    /// distinct BatchSeq exactly once; TrailManager's cursor merge makes overlaps idempotent.
    /// </summary>
    [RequireComponent(typeof(NetworkPlayer))]
    public class NetworkTrailSync : NetworkBehaviour
    {
        public const int MaxBatchPoints = 16;

        [Networked, Capacity(MaxBatchPoints * 4)]
        public NetworkArray<float> Batch { get; }

        [Networked] public int BatchStart { get; set; }   // sequence number of the first point
        [Networked] public int BatchCount { get; set; }
        [Networked] public int BatchSeq { get; set; }
        [Networked] public double OriginLat { get; set; }
        [Networked] public double OriginLon { get; set; }

        private NetworkPlayer _player;
        private int _nextSendSequence;   // authority: oldest unsent sequence number
        private int _lastAppliedSeq;     // proxy: last BatchSeq applied

        private void Awake()
        {
            _player = GetComponent<NetworkPlayer>();
        }

        public override void Spawned()
        {
            _nextSendSequence = 0;
            _lastAppliedSeq = 0;
        }

        public override void FixedUpdateNetwork()
        {
            if (Object.HasStateAuthority) PushLocalTrail();
            else PullRemoteTrail();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Authority → wire
        // ─────────────────────────────────────────────────────────────────────
        private void PushLocalTrail()
        {
            if (!TrailManager.HasInstance) return;
            var trail = TrailManager.Instance.LocalTrail;
            if (trail == null) return;
            if (trail.HighestAppliedSequence < _nextSendSequence) return; // nothing new

            int batchSize = Mathf.Min(GameConfig.Active.trailSyncBatchSize, MaxBatchPoints);
            TrailSnapshot snap = trail.TakeSnapshot(_nextSendSequence, batchSize);
            if (snap.Count == 0) return;

            OriginLat = snap.originLat;
            OriginLon = snap.originLon;
            BatchStart = snap.startIndex;
            BatchCount = snap.Count;
            for (int i = 0; i < snap.Count * 4; i++)
                Batch.Set(i, snap.points[i]);
            BatchSeq++;

            // Advance by exactly the points packed — surplus flows in subsequent ticks (§8.2).
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
            for (int i = 0; i < floats; i++)
                data[i] = Batch.Get(i);

            var snap = new TrailSnapshot(_player.PlayerId.ToString(), BatchStart, OriginLat, OriginLon, data);
            TrailManager.Instance.UpdateRemoteTrail(snap.ownerId, snap);
        }
    }
}
#endif
