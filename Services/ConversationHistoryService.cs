using System.Collections.Concurrent;
using System.Collections.Generic;

namespace VoiceLiveApi.Web.Services
{
    public class ConversationHistoryService : IConversationHistoryService
    {
        private readonly ConcurrentDictionary<string, List<string>> _sessionHistories = new();
        private readonly object _lock = new object();

        public void AddMessage(string sessionId, string message)
        {
            var history = _sessionHistories.GetOrAdd(sessionId, _ => new List<string>());

            lock (_lock)
            {
                history.Add(message);
            }
        }

        public void ClearHistory(string sessionId)
        {
            if (_sessionHistories.TryGetValue(sessionId, out var history))
            {
                lock (_lock)
                {
                    history.Clear();
                }
            }
        }

        public IList<string> GetHistory(string sessionId)
        {
            if (_sessionHistories.TryGetValue(sessionId, out var history))
            {
                lock (_lock)
                {
                    return history.AsReadOnly();
                }
            }

            return new List<string>().AsReadOnly();
        }

        public void RemoveSession(string sessionId)
        {
            _sessionHistories.TryRemove(sessionId, out _);
        }

        public int GetActiveSessionCount()
        {
            return _sessionHistories.Count;
        }
    }
}