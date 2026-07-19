using System.Globalization;
using System.Text;

namespace LightRunners.Core
{
    /// <summary>Small string helpers shared across assemblies. Spec §3.3 (Core).</summary>
    public static class StringUtils
    {
        /// <summary>
        /// Invariant culture number formatting/parsing — never use the current thread culture
        /// for wire formats. Avoids the classic "1,5 vs 1.5" decimal bug across locales.
        /// </summary>
        public static string FormatInvariant(string format, params object[] args)
            => string.Format(CultureInfo.InvariantCulture, format, args);

        public static string ToInvariant(this double v) => v.ToString(CultureInfo.InvariantCulture);
        public static string ToInvariant(this float v) => v.ToString(CultureInfo.InvariantCulture);

        public static bool TryParseDouble(string s, out double v)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        /// <summary>Display-name fallback for anonymous identities: "Runner_" + first 6 hex chars. Spec §12.1.</summary>
        public static string RunnerDisplayName(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return "Runner_anon";
            // Take first 6 alphanumerics of whatever id we have.
            var sb = new StringBuilder(16);
            int taken = 0;
            foreach (char c in userId)
            {
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                {
                    sb.Append(c);
                    if (++taken >= 6) break;
                }
            }
            return "Runner_" + (sb.Length > 0 ? sb.ToString() : "anon");
        }
    }
}
