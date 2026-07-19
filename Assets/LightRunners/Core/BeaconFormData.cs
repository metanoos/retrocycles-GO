using System;
using UnityEngine;

namespace LightRunners.Core
{
    /// <summary>
    /// Static metadata for one beacon form. The full table ships via <see cref="Defaults"/>.
    /// Spec §2.4 / §4.3.
    /// </summary>
    [Serializable]
    public class BeaconFormData
    {
        public BeaconFormType formType;
        public string displayName;
        public string prefabName;
        public Color trailColor;
        public bool unlocked;
        public int requiredLevel;

        // Spec §2.4 colour table. Colors are linear-space RGB in the 0..1 range; converted
        // via the `(value, value, value, 1)` ctor — close enough for selection UI without
        // chasing gamma specifics (designers retune in the inspector).
        public static readonly Color Cyan = new Color(0f, 1f, 1f, 1f);
        public static readonly Color Magenta = new Color(1f, 0.2f, 1f, 1f);
        public static readonly Color Green = new Color(0.2f, 1f, 0.4f, 1f);
        public static readonly Color Orange = new Color(1f, 0.5f, 0f, 1f);
        public static readonly Color Yellow = new Color(1f, 1f, 0.2f, 1f);
        public static readonly Color Red = new Color(1f, 0.1f, 0.1f, 1f);
        public static readonly Color Amber = new Color(1f, 0.6f, 0f, 1f);
        public static readonly Color ElectricBlue = new Color(0.4f, 0.6f, 1f, 1f);

        /// <summary>The canonical 8-form table from spec §2.4. Order is significant (enum order).</summary>
        public static BeaconFormData[] Defaults => new[]
        {
            new BeaconFormData { formType = BeaconFormType.Hoverboard,    displayName = "Hoverboard", prefabName = "Hoverboard",  trailColor = Cyan,         unlocked = true,  requiredLevel = 0 },
            new BeaconFormData { formType = BeaconFormType.Sphere,        displayName = "Orb",        prefabName = "Orb",          trailColor = Magenta,      unlocked = true,  requiredLevel = 0 },
            new BeaconFormData { formType = BeaconFormType.Drone,         displayName = "Drone",      prefabName = "Drone",        trailColor = Green,        unlocked = true,  requiredLevel = 0 },
            new BeaconFormData { formType = BeaconFormType.AbstractShape, displayName = "Prism",      prefabName = "Prism",        trailColor = Orange,       unlocked = false, requiredLevel = 5 },
            new BeaconFormData { formType = BeaconFormType.FloatingCube,  displayName = "Cube",       prefabName = "Cube",         trailColor = Yellow,       unlocked = false, requiredLevel = 10 },
            new BeaconFormData { formType = BeaconFormType.Motorcycle,    displayName = "Runner",     prefabName = "Runner",       trailColor = Red,          unlocked = false, requiredLevel = 15 },
            new BeaconFormData { formType = BeaconFormType.Phoenix,       displayName = "Phoenix",    prefabName = "Phoenix",      trailColor = Amber,        unlocked = false, requiredLevel = 20 },
            new BeaconFormData { formType = BeaconFormType.Waveform,      displayName = "Waveform",   prefabName = "Waveform",     trailColor = ElectricBlue, unlocked = false, requiredLevel = 25 },
        };
    }
}
