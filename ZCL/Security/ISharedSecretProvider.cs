using System;
using System.Collections.Generic;
using System.Text;

namespace ZCL.Security;

public interface ISharedSecretProvider
{
    Task<string?> GetSecretAsync();
    Task SetSecretAsync(string secret);

    string? GetCachedSecret();
}