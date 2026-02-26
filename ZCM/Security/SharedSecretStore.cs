using Microsoft.Maui.Storage;
using ZCL.Security;

namespace ZCM.Security;

public sealed class SharedSecretStore : ISharedSecretProvider
{
    private const string KeyName = "zc_tls_secret_v1";
    private string? _cachedSecret;

    public SharedSecretStore()
    {
        // Load once at startup
        _cachedSecret = SecureStorage.Default
            .GetAsync(KeyName)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    public string? GetSecret()
    {
        return _cachedSecret;
    }

    public void SetSecret(string secret)
    {
        _cachedSecret = secret;

        _ = SecureStorage.Default
            .SetAsync(KeyName, secret); // fire-and-forget async
    }
}