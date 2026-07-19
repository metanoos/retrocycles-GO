using System;
using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Trail
{
    /// <summary>
    /// Capsule-chain tail geometry builder (active decision T). Replaces the flat LineRenderer
    /// ribbon concept with a chain of capsules — one per trail segment — so the visual tail, the
    /// collision volume, and the safety clearances all derive from the SAME authoritative radius.
    ///
    /// A capsule is the Minkowski sum of a line segment and a sphere: it's the smallest convex
    /// shape that covers a tube of radius <c>r</c> around a segment of length <c>L</c>, with hemi-
    /// spherical caps so adjacent capsules join without pinching. That's exactly the shape a
    /// "Snake" runner's tail segment occupies, and exactly the shape a head (modelled as a point
    /// or small sphere) collides with when its distance to the segment's spine drops below
    /// <c>r + headRadius</c>. <see cref="TrailCollisionDetector"/> already does point-to-segment
    /// distance + threshold; this builder formalizes the same geometry into a descriptor that a
    /// renderer can instance as Unity capsule meshes, and that an offline replay can bake.
    ///
    /// PURE C# (no MonoBehaviour) so this is unit-testable in isolation. Track D wires the actual
    /// mesh instancing (one <c>GameObject.CreatePrimitive(PrimitiveType.Capsule)</c> per segment
    /// or an instanced mesh; the choice is the renderer's, not the geometry's). Discontinuity
    /// pairs (SPEC §20 — <c>isSegmentStart</c>) produce NO capsule so a pause/dropout gap is never
    /// drawn as a wall.
    /// </summary>
    public static class CapsuleChainTail
    {
        /// <summary>
        /// One capsule along the tail. <see cref="Start"/>/<see cref="End"/> are the spine
        /// endpoints in world (Unity) space; <see cref="Radius"/> is the tail radius. The capsule
        /// is the set of points within <c>Radius</c> of the spine segment
        /// [<see cref="Start"/>, <see cref="End"/>] (cylinder + two hemispherical caps).
        /// </summary>
        public readonly struct Capsule : IEquatable<Capsule>
        {
            public readonly Vector3 Start;
            public readonly Vector3 End;
            public readonly float Radius;
            /// <summary>Owner of this trail segment (carried through so a renderer can tint per player).</summary>
            public readonly string OwnerId;

            public Capsule(Vector3 start, Vector3 end, float radius, string ownerId)
            {
                Start = start;
                End = end;
                Radius = radius;
                OwnerId = ownerId;
            }

            /// <summary>Spine length (m). Zero for a degenerate (point) capsule.</summary>
            public float Length => Vector3.Distance(Start, End);

            /// <summary>Midpoint of the spine — the natural pivot for a Unity capsule primitive (which is centred on its local origin).</summary>
            public Vector3 Center => (Start + End) * 0.5f;

            /// <summary>Total world length including both hemispherical caps (Length + 2·Radius).</summary>
            public float TotalExtent => Length + Radius + Radius;

            /// <summary>
            /// Shortest distance from <paramref name="point"/> to this capsule's surface (0 if
            /// inside). Used by collision: a head at <paramref name="point"/> with radius
            /// <c>headRadius</c> overlaps the capsule iff
            /// <c>DistanceToSpine(point) &lt; Radius + headRadius</c>.
            /// </summary>
            public double DistanceToSpine(Vector3 point)
            {
                Vector3 ab = End - Start;
                float lenSq = ab.sqrMagnitude;
                float t = lenSq < 1e-8f ? 0f : Mathf.Clamp01(Vector3.Dot(point - Start, ab) / lenSq);
                Vector3 proj = Start + ab * t;
                return Vector3.Distance(point, proj);
            }

            public bool Equals(Capsule other)
                => Start.Equals(other.Start) && End.Equals(other.End)
                   && Radius.Equals(other.Radius) && OwnerId == other.OwnerId;
            public override bool Equals(object obj) => obj is Capsule c && Equals(c);
            public override int GetHashCode() => (Start, End, Radius, OwnerId).GetHashCode();
            public static bool operator ==(Capsule a, Capsule b) => a.Equals(b);
            public static bool operator !=(Capsule a, Capsule b) => !a.Equals(b);

            public override string ToString()
                => $"Capsule[{OwnerId}] {Start}→{End} r={Radius:F2}";
        }

        /// <summary>
        /// Build the capsule chain for a list of trail points. Each pair of consecutive points
        /// whose <c>isSegmentStart</c> flag is NOT set on the second produces one capsule;
        /// discontinuity pairs (SPEC §20) produce none, so a pause/dropout gap is never drawn or
        /// collided as a wall. Points are converted geo→world via
        /// <see cref="CoordinateConverter.GeoToWorld"/>; the caller is responsible for setting the
        /// reference (the trail code already does this).
        /// </summary>
        /// <param name="points">Ordered trail points (oldest first).</param>
        /// <param name="radius">Tail radius (m). Authoritative value comes from <see cref="ITailAuthority.FrozenTailRadius"/>.</param>
        /// <param name="ownerId">Player id tinted onto every capsule.</param>
        /// <param name="output">Filled with one capsule per continuous segment; cleared first. Null-safe.</param>
        public static void Build(IReadOnlyList<TrailPoint> points, float radius, string ownerId, List<Capsule> output)
        {
            if (output == null) return;
            output.Clear();
            if (points == null || points.Count < 2) return;

            float r = Math.Max(0f, radius);
            for (int i = 0; i < points.Count - 1; i++)
            {
                TrailPoint a = points[i];
                TrailPoint b = points[i + 1];
                if (b.isSegmentStart) continue; // discontinuity — not a wall (SPEC §20)

                Vector3 wa = CoordinateConverter.GeoToWorld(a.position);
                Vector3 wb = CoordinateConverter.GeoToWorld(b.position);
                output.Add(new Capsule(wa, wb, r, ownerId));
            }
        }

        /// <summary>Convenience overload returning a fresh list (allocates; prefer the pooled overload in hot paths).</summary>
        public static List<Capsule> Build(IReadOnlyList<TrailPoint> points, float radius, string ownerId)
        {
            var list = new List<Capsule>();
            Build(points, radius, ownerId, list);
            return list;
        }

        /// <summary>
        /// Does <paramref name="head"/> (with radius <paramref name="headRadius"/>) overlap any
        /// capsule in <paramref name="chain"/>? Returns the first hit (or a default with
        /// <c>OwnerId == null</c>). This is the reference collision test for the capsule model;
        /// <see cref="TrailCollisionDetector"/> keeps its own (faster, 2D) path for live play, but
        /// this is the geometric truth both must agree on.
        /// </summary>
        public static bool OverlapsAny(Vector3 head, float headRadius, IReadOnlyList<Capsule> chain, out Capsule hit)
        {
            float rr = headRadius;
            for (int i = 0; i < chain.Count; i++)
            {
                var c = chain[i];
                double d = c.DistanceToSpine(head);
                if (d < c.Radius + rr)
                {
                    hit = c;
                    return true;
                }
            }
            hit = default;
            return false;
        }
    }
}
