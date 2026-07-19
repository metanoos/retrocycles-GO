using System.Collections.Generic;
using UnityEngine;
using LightRunners.Core;

namespace LightRunners.Beacon
{
    /// <summary>
    /// Owns the beacon form table (spec §9.1), loaded from <see cref="BeaconFormData.Defaults"/>.
    /// Unlock state is level-driven (spec §12.5: level = floor(sqrt(km))); the backend pushes
    /// the player's level via <see cref="ApplyPlayerLevel"/> after sign-in / record_run.
    /// Enforcement is client-side only at v1 (cosmetic stakes, spec §22).
    /// </summary>
    public class BeaconFormManager : Singleton<BeaconFormManager>
    {
        private readonly Dictionary<BeaconFormType, BeaconFormData> _forms = new Dictionary<BeaconFormType, BeaconFormData>();

        public BeaconFormType SelectedForm { get; private set; } = BeaconFormType.Hoverboard;

        /// <summary>The player level last pushed from the backend (0 before any sync).</summary>
        public int PlayerLevel { get; private set; }

        public event System.Action<BeaconFormType> OnFormSelected;

        protected override void Awake()
        {
            base.Awake();
            foreach (var d in BeaconFormData.Defaults)
                _forms[d.formType] = d;
        }

        private void OnEnable() => GameEvents.PlayerLevelChanged += ApplyPlayerLevel;
        private void OnDisable() => GameEvents.PlayerLevelChanged -= ApplyPlayerLevel;

        /// <summary>Select a form; refuses locked forms (spec §9.1).</summary>
        public bool SelectForm(BeaconFormType form)
        {
            if (!IsFormUnlocked(form)) return false;
            SelectedForm = form;
            OnFormSelected?.Invoke(form);
            return true;
        }

        public bool IsFormUnlocked(BeaconFormType form)
            => _forms.TryGetValue(form, out var d) && d.unlocked;

        /// <summary>Force-unlock (level sync or debug).</summary>
        public void UnlockForm(BeaconFormType form)
        {
            if (_forms.TryGetValue(form, out var d)) d.unlocked = true;
        }

        /// <summary>Re-derive unlock state from a player level (spec §12.5).</summary>
        public void ApplyPlayerLevel(int level)
        {
            PlayerLevel = level;
            foreach (var d in _forms.Values)
                if (level >= d.requiredLevel) d.unlocked = true;
        }

        public Color GetTrailColor(BeaconFormType form)
            => _forms.TryGetValue(form, out var d) ? d.trailColor : Color.cyan;

        public string GetPrefabName(BeaconFormType form)
            => _forms.TryGetValue(form, out var d) ? d.prefabName : form.ToString();

        public string GetDisplayName(BeaconFormType form)
            => _forms.TryGetValue(form, out var d) ? d.displayName : form.ToString();
    }
}
