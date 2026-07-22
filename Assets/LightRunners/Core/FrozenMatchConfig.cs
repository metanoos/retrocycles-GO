using System;

namespace LightRunners.Core
{
    /// <summary>
    /// Integer, host-authoritative collision configuration frozen at countdown.
    /// Keeping the wire/persistence shape in centimetres avoids floating-point
    /// disagreement between clients and gives replay packages a stable hash.
    /// </summary>
    [Serializable]
    public readonly struct FrozenMatchConfig : IEquatable<FrozenMatchConfig>
    {
        public const int SchemaVersion = 1;
        public const int PlayerHeadRadiusCm = 200;
        public const int DefaultTailRadiusCm = 200;
        public const int MinTailRadiusCm = 150;
        public const int MaxTailRadiusCm = 400;
        public const int TailRadiusStepCm = 50;
        public const int CollisionMicrosegmentCm = 400;

        public int TailRadiusCm { get; }
        public int HeadToTrailCollisionCm { get; }
        public int HeadToHeadCollisionCm { get; }
        public int RespawnTrailClearanceCm { get; }
        public int SpawnExitTrailClearanceCm { get; }
        public int ActiveHeadClearanceCm { get; }
        public uint Hash { get; }

        public float TailRadiusMeters => TailRadiusCm / 100f;
        public float PlayerHeadRadiusMeters => PlayerHeadRadiusCm / 100f;
        public float HeadToTrailCollisionMeters => HeadToTrailCollisionCm / 100f;
        public float HeadToHeadCollisionMeters => HeadToHeadCollisionCm / 100f;
        public float RespawnTrailClearanceMeters => RespawnTrailClearanceCm / 100f;
        public float SpawnExitTrailClearanceMeters => SpawnExitTrailClearanceCm / 100f;
        public float ActiveHeadClearanceMeters => ActiveHeadClearanceCm / 100f;
        public float CollisionMicrosegmentMeters => CollisionMicrosegmentCm / 100f;

        private static readonly FrozenMatchConfig DefaultValue = new FrozenMatchConfig(DefaultTailRadiusCm);
        public static FrozenMatchConfig Default => DefaultValue;

        private FrozenMatchConfig(int tailRadiusCm)
        {
            TailRadiusCm = tailRadiusCm;
            HeadToTrailCollisionCm = tailRadiusCm + PlayerHeadRadiusCm;
            HeadToHeadCollisionCm = PlayerHeadRadiusCm * 2;
            RespawnTrailClearanceCm = HeadToTrailCollisionCm + 800;
            SpawnExitTrailClearanceCm = HeadToTrailCollisionCm + 400;
            ActiveHeadClearanceCm = HeadToHeadCollisionCm + 600;
            Hash = ComputeHash(
                tailRadiusCm,
                PlayerHeadRadiusCm,
                HeadToTrailCollisionCm,
                HeadToHeadCollisionCm,
                RespawnTrailClearanceCm,
                SpawnExitTrailClearanceCm,
                ActiveHeadClearanceCm,
                CollisionMicrosegmentCm);
        }

        public static bool IsLegalTailRadiusCm(int value)
            => value >= MinTailRadiusCm
               && value <= MaxTailRadiusCm
               && (value - MinTailRadiusCm) % TailRadiusStepCm == 0;

        public static bool TryCreateFromMeters(
            float tailRadiusMeters,
            out FrozenMatchConfig config,
            out string error)
        {
            config = default;
            if (float.IsNaN(tailRadiusMeters) || float.IsInfinity(tailRadiusMeters))
            {
                error = "Tail radius must be a finite number.";
                return false;
            }

            int tailRadiusCm = (int)Math.Round(
                tailRadiusMeters * 100.0,
                MidpointRounding.AwayFromZero);
            if (Math.Abs(tailRadiusMeters - tailRadiusCm / 100f) > 0.0001f)
            {
                error = "Tail radius must resolve to a whole centimetre.";
                return false;
            }

            return TryCreate(tailRadiusCm, PlayerHeadRadiusCm, out config, out error);
        }

        public static bool TryCreate(
            int tailRadiusCm,
            int playerHeadRadiusCm,
            out FrozenMatchConfig config,
            out string error)
        {
            config = default;
            if (playerHeadRadiusCm != PlayerHeadRadiusCm)
            {
                error = $"Player collision radius is locked at {PlayerHeadRadiusCm} cm; received {playerHeadRadiusCm} cm.";
                return false;
            }

            if (!IsLegalTailRadiusCm(tailRadiusCm))
            {
                error = $"Tail radius must be {MinTailRadiusCm / 100f:0.0}–{MaxTailRadiusCm / 100f:0.0} m in {TailRadiusStepCm / 100f:0.0} m steps; received {tailRadiusCm / 100f:0.##} m.";
                return false;
            }

            config = new FrozenMatchConfig(tailRadiusCm);
            error = string.Empty;
            return true;
        }

        /// <summary>
        /// Restore or join validation: rederive every value locally and reject a
        /// payload whose player radius or hash differs from the frozen contract.
        /// </summary>
        public static bool TryRestore(
            int tailRadiusCm,
            int playerHeadRadiusCm,
            uint expectedHash,
            out FrozenMatchConfig config,
            out string error)
        {
            if (!TryCreate(tailRadiusCm, playerHeadRadiusCm, out config, out error))
                return false;

            if (config.Hash != expectedHash)
            {
                config = default;
                error = $"Frozen match config hash mismatch (expected {expectedHash:X8}).";
                return false;
            }

            return true;
        }

        private static uint ComputeHash(params int[] values)
        {
            unchecked
            {
                const uint offset = 2166136261u;
                const uint prime = 16777619u;
                uint hash = offset;
                hash = Mix(hash, SchemaVersion, prime);
                for (int i = 0; i < values.Length; i++)
                    hash = Mix(hash, values[i], prime);
                return hash;
            }
        }

        private static uint Mix(uint hash, int value, uint prime)
        {
            unchecked
            {
                hash = (hash ^ (byte)value) * prime;
                hash = (hash ^ (byte)(value >> 8)) * prime;
                hash = (hash ^ (byte)(value >> 16)) * prime;
                hash = (hash ^ (byte)(value >> 24)) * prime;
                return hash;
            }
        }

        public bool Equals(FrozenMatchConfig other)
            => TailRadiusCm == other.TailRadiusCm && Hash == other.Hash;

        public override bool Equals(object obj)
            => obj is FrozenMatchConfig other && Equals(other);

        public override int GetHashCode() => (TailRadiusCm, Hash).GetHashCode();
        public static bool operator ==(FrozenMatchConfig left, FrozenMatchConfig right) => left.Equals(right);
        public static bool operator !=(FrozenMatchConfig left, FrozenMatchConfig right) => !left.Equals(right);

        public override string ToString()
            => $"Tail={TailRadiusMeters:0.0}m Head={PlayerHeadRadiusMeters:0.0}m Hash={Hash:X8}";
    }
}
