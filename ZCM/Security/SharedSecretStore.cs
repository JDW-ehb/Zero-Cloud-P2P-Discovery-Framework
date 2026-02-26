using Microsoft.Maui.Storage;
using ZCL.Security;

public sealed class SharedSecretStore : ISharedSecretProvider
{
    private const string KeyName = "zc_tls_secret_v1";

    private string? _cachedSecret;
    private bool _loaded;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string?> GetSecretAsync()
    {
        if (_loaded)
            return _cachedSecret;

        await _lock.WaitAsync();
        try
        {
            if (_loaded)
                return _cachedSecret;

            try
            {
                _cachedSecret = await SecureStorage.Default.GetAsync(KeyName);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SECURE STORAGE] Failed to load secret:");
                Console.WriteLine(ex);
                _cachedSecret = null;
            }

            _loaded = true;
            return _cachedSecret;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetSecretAsync(string secret)
    {
        await _lock.WaitAsync();
        try
        {
            _cachedSecret = secret;
            _loaded = true;

            await SecureStorage.Default.SetAsync(KeyName, secret);
        }
        finally
        {
            _lock.Release();
        }
    }
    public string? GetCachedSecret()
    {
        return _cachedSecret;
    }
}