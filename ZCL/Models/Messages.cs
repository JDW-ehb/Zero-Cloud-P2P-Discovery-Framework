using System;
using System.Collections.Generic;
using System.Text;

namespace ZCL.Models
{    
    public enum MsgType
    {
        None = 0,
        Announce
    }

    public class Service
    {
        UInt16 port;
        public required string name;
    }

    public class MsgHeader
    {
        public UInt16 version;
        public UInt32 type;
        public UInt64 messageID;
        public UInt128 peerUUID;
    }

    public class MsgAnnounce
    {
        public UInt64 servicesCount;
    }
 
}
