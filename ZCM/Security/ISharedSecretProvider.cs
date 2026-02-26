using System;
using System.Collections.Generic;
using System.Text;

namespace ZCM.Security
{
    public interface ISharedSecretProvider
    {
        void SetSecret(string secret);
        string? GetSecret();
    }
}