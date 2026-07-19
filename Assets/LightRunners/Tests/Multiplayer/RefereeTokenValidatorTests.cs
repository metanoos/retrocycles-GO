using System;
using NUnit.Framework;
using LightRunners.Multiplayer;

namespace LightRunners.Multiplayer.Tests
{
    /// <summary>
    /// Round-trip + negative-path tests for <see cref="RefereeTokenValidator"/>
    /// (decision R). NOT gated on FUSION_WEAVER — the validator is pure C# and
    /// the test must compile and run without the Fusion SDK.
    ///
    /// DIVERGENCE FROM SPEC §8.1: the referee role is new under Host Mode
    /// (decisions Q + R); the SPEC's Shared-Mode narrative has no tokens.
    /// </summary>
    [TestFixture]
    public class RefereeTokenValidatorTests
    {
        private const string MatchId = "match-zone_37.7_-122.4-1750123456";
        private const string Secret = "host-secret-do-not-ship-in-defaults";

        // ─────────────────────────────────────────────────────────────────────
        // Round-trip
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void Issue_Then_Validate_AcceptsTheToken()
        {
            string token = RefereeTokenValidator.Issue(MatchId, Secret);

            Assert.IsTrue(RefereeTokenValidator.Validate(token, MatchId, Secret),
                "Round-tripped token must validate against the issuing (matchId, secret).");
        }

        [Test]
        public void Issue_IsDeterministic_ForSameInputs()
        {
            // The contract is "deterministic" — tests can predict the token.
            string a = RefereeTokenValidator.Issue(MatchId, Secret);
            string b = RefereeTokenValidator.Issue(MatchId, Secret);

            Assert.AreEqual(a, b,
                "Same (matchId, secret) must produce the same token.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Negative paths
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void Validate_RejectsEmptyToken()
        {
            Assert.IsFalse(RefereeTokenValidator.Validate("", MatchId, Secret));
            Assert.IsFalse(RefereeTokenValidator.Validate(null, MatchId, Secret));
        }

        [Test]
        public void Validate_RejectsEmptyMatchId()
        {
            string token = RefereeTokenValidator.Issue(MatchId, Secret);
            Assert.IsFalse(RefereeTokenValidator.Validate(token, "", Secret));
            Assert.IsFalse(RefereeTokenValidator.Validate(token, null, Secret));
        }

        [Test]
        public void Validate_RejectsEmptySecret()
        {
            string token = RefereeTokenValidator.Issue(MatchId, Secret);
            Assert.IsFalse(RefereeTokenValidator.Validate(token, MatchId, ""));
            Assert.IsFalse(RefereeTokenValidator.Validate(token, MatchId, null));
        }

        [Test]
        public void Validate_RejectsWrongSecret()
        {
            string token = RefereeTokenValidator.Issue(MatchId, Secret);

            Assert.IsFalse(RefereeTokenValidator.Validate(token, MatchId, "different-secret"),
                "A token issued under one secret must NOT validate under another.");
        }

        [Test]
        public void Validate_RejectsWrongMatchId()
        {
            string token = RefereeTokenValidator.Issue(MatchId, Secret);

            Assert.IsFalse(RefereeTokenValidator.Validate(token, "match-something-else", Secret),
                "A token bound to matchId A must NOT validate for matchId B.");
        }

        [Test]
        public void Validate_RejectsMalformedBase64()
        {
            // '!' is not a base64url character; FromBase64String throws → reject.
            Assert.IsFalse(RefereeTokenValidator.Validate("not!!!valid!!!base64!!!", MatchId, Secret));
        }

        [Test]
        public void Validate_RejectsTruncatedToken()
        {
            // A valid HMAC-SHA256 token is 32 bytes / 43 base64url chars.
            // Truncate to something the length check should refuse.
            string token = RefereeTokenValidator.Issue(MatchId, Secret);
            string truncated = token.Substring(0, 8); // ~6 bytes worth — well below MinRawHmacBytes

            Assert.IsFalse(RefereeTokenValidator.Validate(truncated, MatchId, Secret),
                "A token that decodes to fewer than MinRawHmacBytes must be rejected.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Token format / length
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void Issue_ProducesExpectedLength()
        {
            // HMAC-SHA256 → 32 bytes → 43 base64url chars (unpadded).
            string token = RefereeTokenValidator.Issue(MatchId, Secret);

            Assert.AreEqual(43, token.Length,
                $"Expected 43-char base64url token (32 raw bytes, unpadded); got {token.Length}.");
        }

        [Test]
        public void Issue_ProducesUrlSafeAlphabet()
        {
            string token = RefereeTokenValidator.Issue(MatchId, Secret);

            foreach (char c in token)
            {
                bool ok = (c >= 'A' && c <= 'Z')
                          || (c >= 'a' && c <= 'z')
                          || (c >= '0' && c <= '9')
                          || c == '-' || c == '_';
                Assert.IsTrue(ok, $"Token contains non-base64url char '{c}'.");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Constant-time smoke check
        //
        // We cannot measure timing reliably from a unit test, but we CAN assert
        // that flipping a single bit of the secret produces a token that fails
        // validation and that the failure path returns false (not throws). The
        // length-mismatch short-circuit is documented; here we ensure that the
        // happy and the wrong-secret paths both touch the constant-time compare
        // with equal-length inputs (the common production case).
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void Validate_SingleBitDifferenceInSecret_Rejects()
        {
            string token = RefereeTokenValidator.Issue(MatchId, Secret);

            // Flip one character of the secret — XOR-by-one if it is a digit/letter.
            char last = Secret[Secret.Length - 1];
            char flipped = (char)(last ^ 1);
            string tamperedSecret = Secret.Substring(0, Secret.Length - 1) + flipped;

            // The tampered secret produces a different (same-length) HMAC; the
            // validator must compare equal-length arrays in constant time and
            // return false without leaking which byte differed.
            Assert.IsFalse(RefereeTokenValidator.Validate(token, MatchId, tamperedSecret));
        }

        [Test]
        public void Validate_TamperedTokenByte_Rejects()
        {
            string token = RefereeTokenValidator.Issue(MatchId, Secret);

            // Flip one character of the token — same length, must hit the
            // constant-time path and reject.
            char[] chars = token.ToCharArray();
            chars[5] = (char)(chars[5] ^ 1);
            string tampered = new string(chars);

            Assert.IsFalse(RefereeTokenValidator.Validate(tampered, MatchId, Secret));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Argument checking on Issue (the host-side producer)
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        public void Issue_ThrowsOnNullArgs()
        {
            Assert.Throws<ArgumentNullException>(() => RefereeTokenValidator.Issue(null, Secret));
            Assert.Throws<ArgumentNullException>(() => RefereeTokenValidator.Issue(MatchId, null));
        }

        [Test]
        public void Issue_ThrowsOnEmptyArgs()
        {
            Assert.Throws<ArgumentException>(() => RefereeTokenValidator.Issue("", Secret));
            Assert.Throws<ArgumentException>(() => RefereeTokenValidator.Issue(MatchId, ""));
        }
    }
}
