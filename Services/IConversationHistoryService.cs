using System.Collections.Generic;

namespace VoiceLiveApi.Web.Services
{
    public interface IConversationHistoryService
    {
        void AddMessage(string message);
        void ClearHistory();
        IList<string> GetHistory();
    }
}