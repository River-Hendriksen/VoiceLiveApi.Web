using System.Collections.Generic;

namespace VoiceLiveApi.Web.Services
{
    public interface IConversationHistoryService
    {
        void AddMessage(string sessionId, string message);
        void ClearHistory(string sessionId);
        IList<string> GetHistory(string sessionId);
        void RemoveSession(string sessionId);
        int GetActiveSessionCount();
    }
}