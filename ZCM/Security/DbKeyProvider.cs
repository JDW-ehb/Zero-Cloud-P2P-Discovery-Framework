using Microsoft.Maui.Storage;
using System.Security.Cryptography;

namespace ZCM.Security;

public static class DbKeyProvider
{
    private const string KeyName = "zc_sqlcipher_key_v1";

    public static string GetKey()
    {
        var existing = SecureStorage.Default
            .GetAsync(KeyName)
            .GetAwaiter()
            .GetResult();

        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var bytes = RandomNumberGenerator.GetBytes(32);
        var key = Convert.ToHexString(bytes);

        SecureStorage.Default
            .SetAsync(KeyName, key)
            .GetAwaiter()
            .GetResult();

        return key;
    }
}