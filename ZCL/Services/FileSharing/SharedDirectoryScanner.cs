using System.Security.Cryptography;
using ZCL.Models;

namespace ZCL.Services.FileSharing;

public sealed class SharedDirectoryScanner
{
    public static IEnumerable<SharedFileEntity> Scan(
        Guid localPeerId,
        string sharedDir)
    {
        Directory.CreateDirectory(sharedDir);

        foreach (var path in Directory.GetFiles(sharedDir))
        {
            var info = new FileInfo(path);

            yield return new SharedFileEntity
            {
                FileId = Guid.NewGuid(),
                PeerRefId = localPeerId,
                FileName = info.Name,
                FileSize = info.Length,
                FileType = info.Extension.TrimStart('.'),
                Checksum = ComputeSha256(path),
                LocalPath = path,
                SharedSince = info.CreationTimeUtc,
                IsAvailable = true
            };
        }
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
