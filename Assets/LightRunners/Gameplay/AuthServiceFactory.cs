using LightRunners.Core;
using LightRunners.Identity;
using LightRunners.Backend;

namespace LightRunners.Gameplay
{
    /// <summary>
    /// One place decides which IAuthService backs the game (spec §12.1): Supabase when
    /// configured, the no-network stub otherwise. Both LoginUI and PlatformServiceRegistry
    /// route through here so the choice can't fork.
    /// </summary>
    public static class AuthServiceFactory
    {
        public static IAuthService Create()
        {
            var cfg = GameConfig.Active;
            if (!string.IsNullOrEmpty(cfg.supabaseUrl) && !string.IsNullOrEmpty(cfg.supabaseAnonKey))
                return new SupabaseAuthService(SupabaseManager.Ensure());
            return new EditorAnonymousAuthService();
        }
    }
}
