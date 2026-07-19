using UnityEngine;

namespace LightRunners.Afterglow
{
    /// <summary>
    /// Placeholder for the Afterglow Walk-Inside view (decision S). The ground-only
    /// milestone ships Overview only; Walk-Inside unlocks after the aerial milestone.
    /// Provides a no-op <see cref="Show"/> that <see cref="AfterglowViewController"/>
    /// delegates to. Decision U: when wired up for real, it will read the same
    /// <see cref="ReplayPackage"/> as Overview, preserving selection/focus.
    /// </summary>
    public sealed class WalkInsideStub
    {
        public const string LockedMessage =
            "Afterglow Walk-Inside unlocks after the aerial milestone (decision S).";

        /// <summary>
        /// No-op Show entry point. Logs the locked message at warning level so QA sees it
        /// in the console. Returns without rendering anything.
        /// </summary>
        public void Show(ReplayPackage package)
        {
            Debug.LogWarning($"[WalkInsideStub] {LockedMessage}");
        }
    }
}
