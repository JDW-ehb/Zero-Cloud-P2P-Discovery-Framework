using System;

namespace ZCL.Security
{
    internal static class TlsConstants
    {
        public const string NetworkProofOid = "1.3.6.1.4.1.55555.1.99";

        public const string MembershipTagOid = "1.3.6.1.4.1.55555.1.1";

        public const string MembershipTagPrefix = "ZC-TAG:v1:";

        public const string SubjectCnPrefix = "ZC Peer";

        public const string DefaultPfxFileName = "zc_tls_identity.pfx";

        public const string DefaultPfxPassword = "zc_dev_pfx_password_change_me";
    }
}