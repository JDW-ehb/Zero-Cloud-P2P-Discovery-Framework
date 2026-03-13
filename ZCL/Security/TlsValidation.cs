using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ZCL.Security
{
    internal static class TlsValidation
    {
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

            var tagExts = cert.Extensions
                .OfType<X509Extension>()
                .Where(e => e.Oid?.Value == TlsConstants.MembershipTagOid)
                .ToList();

            if (tagExts.Count == 0)
            {
                reason = "No membership proof found on certificate.";
                return false;
            }

            var tagHexes = new List<string>();

            foreach (var ext in tagExts)
            {
                var payload = TryDecodeUtf8(ext.RawData);
                if (payload == null)
                    continue;

                if (!payload.StartsWith(TlsConstants.MembershipTagPrefix, StringComparison.Ordinal))
                    continue;

                var tagHex = payload.Substring(TlsConstants.MembershipTagPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(tagHex))
                    tagHexes.Add(tagHex);
            }

            if (tagHexes.Count == 0)
            {
                reason = "Membership extensions present but invalid format.";
                return false;
            }

            foreach (var secret in trustedSecrets)
            {
                var expected = TlsCertificateProvider.ComputeMembershipTagHex(cert.PublicKey, secret);

                foreach (var tagHex in tagHexes)
                {
                    if (ConstantTimeEqualsHex(tagHex, expected))
                    {
                        reason = "Trusted (at least one group tag matched).";
                        return true;
                    }
                }
            }

            reason = "No matching group tag found (no enabled group matched).";
            return false;
        }

        private static string? TryDecodeUtf8(byte[] bytes)
        {
            try { return Encoding.UTF8.GetString(bytes); }
            catch { return null; }
        }

        private static bool ConstantTimeEqualsHex(string aHex, string bHex)
        {
            aHex = aHex.Trim();
            bHex = bHex.Trim();

            if (aHex.Length != bHex.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < aHex.Length; i++)
                diff |= (aHex[i] ^ bHex[i]);

            return diff == 0;
        }
    }
}