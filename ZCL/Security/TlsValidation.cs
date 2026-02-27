using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ZCL.Security
{
    internal static class TlsValidation
    {
        /// <summary>
        /// Returns true if the certificate contains a valid membership tag
        /// derived from the shared secret.
        /// </summary>
        public static bool IsTrustedPeerCertificate(
             X509Certificate2? cert,
             IEnumerable<byte[]> trustedSecrets,
             out string reason)
        {
            reason = "Unknown";

            if (cert == null)
            {
                reason = "No certificate provided.";
                return false;
            }

            var ext = cert.Extensions
                .OfType<X509Extension>()
                .FirstOrDefault(e => e.Oid?.Value == TlsConstants.MembershipTagOid);

            if (ext == null)
            {
                reason = "No membership proof found on certificate.";
                return false;
            }

            var payload = TryDecodeUtf8(ext.RawData);
            if (payload == null)
            {
                reason = "Membership extension present but not UTF-8.";
                return false;
            }

            if (!payload.StartsWith(TlsConstants.MembershipTagPrefix, StringComparison.Ordinal))
            {
                reason = "Membership extension has wrong prefix/version.";
                return false;
            }

            var tagHex = payload.Substring(TlsConstants.MembershipTagPrefix.Length).Trim();
            if (tagHex.Length == 0)
            {
                reason = "Membership extension tag missing.";
                return false;
            }

            foreach (var secret in trustedSecrets)
            {
                var expected = TlsCertificateProvider.ComputeMembershipTagHex(cert.PublicKey, secret);
                if (ConstantTimeEqualsHex(tagHex, expected))
                {
                    reason = "Trusted (group tag ok).";
                    return true;
                }
            }

            reason = "Membership tag mismatch (no enabled group matched).";
            return false;
        }

        private static string? TryDecodeUtf8(byte[] bytes)
        {
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }

        private static bool ConstantTimeEqualsHex(string aHex, string bHex)
        {
            aHex = aHex.Trim();
            bHex = bHex.Trim();

            if (aHex.Length != bHex.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < aHex.Length; i++)
            {
                diff |= (aHex[i] ^ bHex[i]);
            }
            return diff == 0;
        }
    }
}