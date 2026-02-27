using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using ZCL.Security;

namespace ZCL.Security
{
    public sealed class TrustGroupCache
    {
        private ImmutableArray<string> _enabledSecretsHex = ImmutableArray<string>.Empty;
        private string? _activeSecretHex;

        public IReadOnlyList<string> EnabledSecretsHex => _enabledSecretsHex;
        public string? ActiveSecretHex => _activeSecretHex;

        public void SetEnabledSecrets(IEnumerable<string> secretsHex)
            => _enabledSecretsHex = secretsHex
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToImmutableArray();

        public void SetActiveSecret(string? secretHex)
            => _activeSecretHex = string.IsNullOrWhiteSpace(secretHex) ? null : secretHex.Trim();
    }
}
