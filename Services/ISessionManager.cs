using VoiceLiveApi.Web.Models;

namespace VoiceLiveApi.Web.Services
{
    public interface ISessionManager
    {
        string CreateSession();
        ChatSession? GetSession(string sessionId);
        bool RemoveSession(string sessionId);
        void CleanupExpiredSessions();
        int GetActiveSessionCount();
        IEnumerable<string> GetActiveSessions();
    }
}