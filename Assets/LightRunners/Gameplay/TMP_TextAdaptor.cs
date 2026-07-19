using UnityEngine;
using UnityEngine.UI;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// Tiny text adaptor that targets TextMeshPro if present, else falls back to the legacy
    /// <see cref="UnityEngine.UI.Text"/>. Lets HUD/summary code be written once and work in
    /// projects with or without TMP imported (the spec assumes TMP, but we don't want a hard
    /// compile dependency just for label strings). Wire the serialized field on the same GO
    /// that has the text component.
    /// </summary>
    [ExecuteAlways]
    public class TMP_TextAdaptor : MonoBehaviour
    {
        [SerializeField] private string text = "";
        [Tooltip("Auto-found on Awake: a TMP_Text or a legacy UI Text on this GO or its children.")]
        [SerializeField] private Component _textComponent;

        private bool _isTMP;

        public string Text
        {
            get => text;
            set => SetText(value);
        }

        private void Awake() => ResolveComponent();

        private void ResolveComponent()
        {
            if (_textComponent != null) { Classify(_textComponent); return; }

            // Look for a TMP_Text by type name first (no compile dep).
            Component found = GetComponentByName("TMPro.TMP_Text");
            if (found == null) found = GetComponentInChildren<Text>();
            if (found != null)
            {
                _textComponent = found;
                Classify(found);
            }
        }

        private void Classify(Component c) => _isTMP = c != null && c.GetType().FullName == "TMPro.TMP_Text";

        private Component GetComponentByName(string fullTypeName)
        {
            foreach (var c in GetComponents<Component>())
                if (c != null && c.GetType().FullName == fullTypeName) return c;
            foreach (var c in GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().FullName == fullTypeName) return c;
            return null;
        }

        public void SetText(string value)
        {
            text = value ?? "";
            if (_textComponent == null) ResolveComponent();
            if (_textComponent == null) return;

            if (_isTMP)
            {
                var prop = _textComponent.GetType().GetProperty("text");
                prop?.SetValue(_textComponent, text);
            }
            else if (_textComponent is Text legacy)
            {
                legacy.text = text;
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Don't resolve during edit-time validation that may run before TMP exists.
        }
#endif
    }
}
