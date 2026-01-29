using System.Collections.Generic;

namespace ZCL.Services.Messaging
{
    public interface IMessageService
    {
        
        ChatMessage Store(string fromPeer, string toPeer, string content);

        IReadOnlyList<ChatMessage> GetConversation(string peerId);
        

    }
}
