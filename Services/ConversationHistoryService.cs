using System.Collections.Generic;

namespace VoiceLiveApi.Web.Services
{
    public class ConversationHistoryService : IConversationHistoryService
    {
        private readonly IList<string> _conversationHistory = new List<string>();
        private readonly object _lock = new object();

        public void AddMessage(string message)
        {
            lock (_lock)
            {
                _conversationHistory.Add(message);
            }
        }

        public void ClearHistory()
        {
            lock (_lock)
            {
                _conversationHistory.Clear();
            }
        }

        public IList<string> GetHistory()
        {
            lock (_lock)
            {
                return _conversationHistory.AsReadOnly();
            }
        }
    }
}