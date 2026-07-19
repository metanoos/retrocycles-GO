using System;

namespace LightRunners.Core
{
    /// <summary>
    /// Ephemeral identity — created anonymously, resumed via a refresh token (spec §4.3 / §12.1).
    /// </summary>
    [Serializable]
    public struct PlayerIdentity
    {
        /// <summary>The auth.uid UUID from Supabase (or, in editor, a generated GUID).</summary>
        public string userId;

        public string displayName;

        public bool IsValid => !string.IsNullOrEmpty(userId);

        public PlayerIdentity(string userId, string displayName)
        {
            this.userId = userId;
            this.displayName = displayName;
        }
    }
}
