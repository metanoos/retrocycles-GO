using LightRunners.Core;

namespace LightRunners.AR
{
    /// <summary>
    /// AR lifecycle seam (spec §11.1) so <c>ViewTransitionManager</c> can drive AR without
    /// referencing AR Foundation — it resolves this interface reflectively via
    /// <c>ServiceLocator.GetByInterfaceName("LightRunners.AR.IARViewController")</c>.
    /// Deliberately NOT gated on UNITY_XR_ARFOUNDATION: the interface has no AR Foundation
    /// types in its signature, so it always compiles; only the implementation is gated.
    /// </summary>
    public interface IARViewController
    {
        void EnterAR();
        void ExitAR();
        bool IsARAvailable { get; }
        bool IsARActive { get; }
        void LoadNearbyTrails(GeoPoint center);
        void UpdateARHeightOffset(float offset);
    }
}
