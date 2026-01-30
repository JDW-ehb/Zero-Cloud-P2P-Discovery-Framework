using System.Collections.Generic;
using ZCL.Services.Messaging;
using ZCL.Services.Messaging.ZCL.Services.Messaging;

namespace ZCL.Services.Messaging
{
    public interface IMessageService
    {
        ChatMessage Store(string fromPeer, string toPeer, string content);
        IReadOnlyList<ChatMessage> GetConversation(string peerId);
    }
}
