using System;
using System.Security.Cryptography;
using System.Text;

namespace LightRunners.Multiplayer
{
    /// <summary>
    /// PURE C# referee-token validator (decision R). NOT gated on FUSION_WEAVER
    /// so it can be unit-tested without the Fusion SDK and so the validation
    /// logic is independently auditable from the RPC plumbing.
    ///
    /// DIVERGENCE FROM SPEC §8.1: the referee role is new under Host Mode
    /// (decisions Q + R); tokens did not exist under Shared Mode.
    ///
    /// TOKEN FORMAT (milestone):
    ///   <base64url(HMACSHA256(secret, UTF8(matchId)))>
    ///
    /// i.e. the HMAC-SHA256 of the UTF-8 bytes of <c>matchId</c> keyed by
    /// <c>hostIssuedSecret</c>, base64url-encoded (no padding). Deterministic:
    /// the same (matchId, secret) always produces the same token, so tests can
    /// verify round-trip Issue→Validate without seeded randomness.
    ///
    /// v2 PRODUCTION CONCERNS (intentionally deferred):
    ///   • Token rotation / expiry (currently a token is valid for the match
    ///     indefinitely).
    ///   • Per-referee scoping (anyone holding the secret can issue a token; in
    ///     production the host should bind the token to a specific referee id).
    ///   • Replay protection across matches (the validator currently only checks
    ///     the match id, not a nonce).
    ///   • Constant-time comparison is implemented for the HMAC compare itself
    ///     (see <see cref="ConstantTimeEquals"/>); the base64 decode + length
    ///     check are NOT constant-time, which is acceptable because they do not
    ///     depend on secret bytes.
    /// </summary>
    public static class RefereeTokenValidator
    {
        /// <summary>
        /// Minimum byte length of the raw HMAC before base64url encoding
        /// (HMAC-SHA256 always produces 32 bytes; this guards against a
        /// truncated / malformed token that happens to decode).
        /// </summary>
        public const int MinRawHmacBytes = 32;

        /// <summary>Maximum byte length accepted for the raw HMAC (post-decode).</summary>
        public const int MaxRawHmacBytes = 64;

        /// <summary>
        /// Issue a referee token for <paramref name="matchId"/> keyed by
        /// <paramref name="hostIssuedSecret"/>. The host calls this when it
        /// accepts a referee connection; the resulting token is handed to the
        /// referee out-of-band (e.g. via the match lobby) and presented back on
        /// each Gate-Director RPC. Deterministic — safe to call repeatedly.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="matchId"/> or <paramref name="hostIssuedSecret"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="matchId"/> or <paramref name="hostIssuedSecret"/> is empty.
        /// </exception>
        public static string Issue(string matchId, string hostIssuedSecret)
        {
            if (matchId == null) throw new ArgumentNullException(nameof(matchId));
            if (hostIssuedSecret == null) throw new ArgumentNullException(nameof(hostIssuedSecret));
            if (matchId.Length == 0) throw new ArgumentException("matchId must be non-empty.", nameof(matchId));
            if (hostIssuedSecret.Length == 0) throw new ArgumentException("hostIssuedSecret must be non-empty.", nameof(hostIssuedSecret));

            byte[] key = Encoding.UTF8.GetBytes(hostIssuedSecret);
            byte[] message = Encoding.UTF8.GetBytes(matchId);
            byte[] hmac;
            using (var algorithm = new HMACSHA256(key))
            {
                hmac = algorithm.ComputeHash(message);
            }
            return Base64UrlEncode(hmac);
        }

        /// <summary>
        /// Validate a referee token against the host-issued secret for the given
        /// match. Returns true iff:
        ///   • <paramref name="token"/> is non-null and non-empty,
        ///   • <paramref name="matchId"/> is non-null and non-empty,
        ///   • <paramref name="hostIssuedSecret"/> is non-null and non-empty,
        ///   • the token base64url-decodes to a byte sequence whose length is in
        ///     [<see cref="MinRawHmacBytes"/>, <see cref="MaxRawHmacBytes"/>],
        ///   • AND the decoded bytes equal the HMAC of <paramref name="matchId"/>
        ///     keyed by <paramref name="hostIssuedSecret"/> (constant-time).
        ///
        /// Any failure short-circuits to false (no exception) so callers can use
        /// this directly in RPC handlers without try/catch noise.
        /// </summary>
        public static bool Validate(string token, string matchId, string hostIssuedSecret)
        {
            if (string.IsNullOrEmpty(token)) return false;
            if (string.IsNullOrEmpty(matchId)) return false;
            if (string.IsNullOrEmpty(hostIssuedSecret)) return false;

            byte[] presented;
            try
            {
                presented = Base64UrlDecode(token);
            }
            catch
            {
                // Malformed base64 — reject without surfacing the parse error.
                return false;
            }

            if (presented.Length < MinRawHmacBytes || presented.Length > MaxRawHmacBytes)
                return false;

            byte[] expected = ComputeExpectedHmac(matchId, hostIssuedSecret);
            // expected.Length is always MinRawHmacBytes (32); presented may differ
            // in length, in which case ConstantTimeEquals returns false.
            return ConstantTimeEquals(presented, expected);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internals — pure and allocation-disciplined. Kept internal so the public
        // surface stays tiny; tests assert behaviour through Issue/Validate only.
        // ─────────────────────────────────────────────────────────────────────

        private static byte[] ComputeExpectedHmac(string matchId, string hostIssuedSecret)
        {
            byte[] key = Encoding.UTF8.GetBytes(hostIssuedSecret);
            byte[] message = Encoding.UTF8.GetBytes(matchId);
            using (var algorithm = new HMACSHA256(key))
            {
                return algorithm.ComputeHash(message);
            }
        }

        /// <summary>
        /// Constant-time byte comparison. Length mismatch returns false (the
        /// length itself is not secret in our use because expected.Length is
        /// always 32; the value bytes are what must not leak).
        /// </summary>
        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>Base64URL encode (RFC 4648 §5, no padding) — URL-safe token.</summary>
        private static string Base64UrlEncode(byte[] bytes)
        {
            string s = Convert.ToBase64String(bytes);
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '+': sb.Append('-'); break;
                    case '/': sb.Append('_'); break;
                    case '=': /* drop padding */ break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Base64URL decode (accepts with or without padding; tolerates +/- as well).</summary>
        private static byte[] Base64UrlDecode(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            // Translate URL-safe alphabet back to the standard one, then pad.
            int rawLen = s.Length;
            StringBuilder sb = new StringBuilder(rawLen + 4);
            for (int i = 0; i < rawLen; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '-': sb.Append('+'); break;
                    case '_': sb.Append('/'); break;
                    default: sb.Append(c); break;
                }
            }
            // Pad to a multiple of 4.
            int pad = (4 - (sb.Length % 4)) % 4;
            if (pad > 0) sb.Append('=', pad);
            return Convert.FromBase64String(sb.ToString());
        }
    }
}
