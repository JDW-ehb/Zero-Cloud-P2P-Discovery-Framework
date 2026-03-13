using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ZCL.Security
{
    public static class TlsCertificateProvider
    {
        public static X509Certificate2 CreateNetworkIdentityCertificate(
            string peerLabel,
            byte[] networkSecret)
        {
            using var rsa = RSA.Create(3072);

            var subject = new X500DistinguishedName($"CN=ZC Peer - {peerLabel}");

            var req = new CertificateRequest(subject, rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            req.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, true));

            req.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature |
                    X509KeyUsageFlags.KeyEncipherment,
                    true));
            var proof = new HMACSHA256(networkSecret)
            .ComputeHash(req.PublicKey.EncodedKeyValue.RawData);

            var payload = Convert.ToHexString(proof);

            req.CertificateExtensions.Add(
                new X509Extension(
                    new Oid("1.3.6.1.4.1.55555.1.99"),
                    Encoding.UTF8.GetBytes(payload),
                    false));


            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = DateTimeOffset.UtcNow.AddYears(5);

            var cert = req.CreateSelfSigned(notBefore, notAfter);

            return new X509Certificate2(
                cert.Export(X509ContentType.Pfx, TlsConstants.DefaultPfxPassword),
                TlsConstants.DefaultPfxPassword,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }

        public static void SavePfx(X509Certificate2 cert, string pfxPath, string password)
        {
            if (cert == null) throw new ArgumentNullException(nameof(cert));
            if (string.IsNullOrWhiteSpace(pfxPath)) throw new ArgumentException("pfxPath required.", nameof(pfxPath));

            var bytes = cert.Export(X509ContentType.Pfx, password);
            File.WriteAllBytes(pfxPath, bytes);
        }

        private static string EscapeCn(string s)
            => s.Replace(",", "_").Replace(";", "_").Replace("\n", "_").Replace("\r", "_");

        public static void DeleteLocalIdentityCertificate(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
                throw new ArgumentException(nameof(baseDirectory));

            var pfxPath = Path.Combine(baseDirectory, TlsConstants.DefaultPfxFileName);

            if (File.Exists(pfxPath))
                File.Delete(pfxPath);
        }

        public static string ComputeMembershipTagHex(PublicKey publicKey, byte[] secretBytes)
        {
            if (publicKey == null) throw new ArgumentNullException(nameof(publicKey));
            if (secretBytes == null || secretBytes.Length == 0) throw new ArgumentException("secretBytes required.", nameof(secretBytes));

            var alg = publicKey.EncodedParameters?.RawData ?? Array.Empty<byte>();
            var key = publicKey.EncodedKeyValue?.RawData ?? Array.Empty<byte>();

            var material = new byte[alg.Length + key.Length];
            Buffer.BlockCopy(alg, 0, material, 0, alg.Length);
            Buffer.BlockCopy(key, 0, material, alg.Length, key.Length);

            using var hmac = new HMACSHA256(secretBytes);
            var tag = hmac.ComputeHash(material);
            return Convert.ToHexString(tag);
        }
    }
}