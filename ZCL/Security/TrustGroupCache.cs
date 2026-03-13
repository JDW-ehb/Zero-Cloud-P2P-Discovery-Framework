using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ZCL.Security
{
    public sealed class TrustGroupCache
    {
        private string[] _enabledSecretsHex = Array.Empty<string>();

        public IReadOnlyList<string> EnabledSecretsHex => _enabledSecretsHex;

        public void SetEnabledSecrets(IEnumerable<string> secretsHex)
        {
            var arr = (secretsHex ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToArray();

            Interlocked.Exchange(ref _enabledSecretsHex, arr);
        }
    }
}