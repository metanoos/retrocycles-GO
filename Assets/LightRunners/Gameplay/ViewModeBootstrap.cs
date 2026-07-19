using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// Decision H — sets the default <see cref="ViewMode"/> to <see cref="ViewMode.AR"/> on
    /// scene load. AR is now the primary view; the map is the corner radar / toggle target.
    ///
    /// Lives in the Gameplay assembly because it must talk to <see cref="GameManager"/> (which
    /// owns the ViewMode property). Add this as a component on the GameManager GameObject (or
    /// any scene-root object that awakens after GameManager) — it self-runs once on Start.
    /// </summary>
    public class ViewModeBootstrap : MonoBehaviour
    {
        [Tooltip("When true, force AR on Start even if a saved pref or another component set Map.")]
        [SerializeField] private bool forceOnStart = true;

        private void Start()
        {
            if (!forceOnStart) return;
            if (!GameManager.HasInstance) return;

            var gm = GameManager.Instance;
            if (gm.ViewMode != ViewMode.AR)
                gm.SetViewMode(ViewMode.AR);
        }
    }
}
