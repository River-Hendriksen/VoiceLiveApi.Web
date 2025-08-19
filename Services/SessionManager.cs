using System.Collections.Concurrent;
using System.Net.WebSockets;
using VoiceLiveApi.Web.Models;

namespace VoiceLiveApi.Web.Services
{
    public class SessionManager : ISessionManager, IDisposable
    {
        private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();
        private readonly ILogger<SessionManager> _logger;
        private readonly Timer _cleanupTimer;
        private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(30); // 30 minutes timeout

        public SessionManager(ILogger<SessionManager> logger)
        {
            _logger = logger;
            
            // Setup cleanup timer to run every 5 minutes
            _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
            
            _logger.LogInformation("SessionManager initialized with {TimeoutMinutes} minute session timeout", _sessionTimeout.TotalMinutes);
        }

        public string CreateSession()
        {
            var sessionId = Guid.NewGuid().ToString();
            var session = new ChatSession { SessionId = sessionId };
            
            _sessions.TryAdd(sessionId, session);
            _logger.LogInformation("Created new session: {SessionId}", sessionId);
            
            return sessionId;
        }

        public ChatSession? GetSession(string sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.UpdateActivity();
                return session;
            }
            return null;
        }

        public bool RemoveSession(string sessionId)
        {
            if (_sessions.TryRemove(sessionId, out var session))
            {
                // Cleanup WebSocket if it exists
                if (session.WebSocket != null)
                {
                    try
                    {
                        if (session.WebSocket.State == WebSocketState.Open)
                        {
                            session.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                        }
                        session.WebSocket.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error closing WebSocket for session {SessionId}", sessionId);
                    }
                }

                _logger.LogInformation("Removed session: {SessionId}", sessionId);
                return true;
            }
            return false;
        }

        public void CleanupExpiredSessions()
        {
            CleanupExpiredSessions(null);
        }

        private void CleanupExpiredSessions(object? state)
        {
            var expiredSessions = _sessions.Where(kvp => kvp.Value.IsExpired(_sessionTimeout)).ToList();
            
            foreach (var kvp in expiredSessions)
            {
                _logger.LogInformation("Cleaning up expired session: {SessionId}", kvp.Key);
                RemoveSession(kvp.Key);
            }

            if (expiredSessions.Any())
            {
                _logger.LogInformation("Cleaned up {Count} expired sessions", expiredSessions.Count);
            }
        }

        public int GetActiveSessionCount()
        {
            return _sessions.Count;
        }

        public IEnumerable<string> GetActiveSessions()
        {
            return _sessions.Keys.ToList();
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
            
            // Cleanup all sessions
            foreach (var session in _sessions.Values)
            {
                if (session.WebSocket != null)
                {
                    try
                    {
                        if (session.WebSocket.State == WebSocketState.Open)
                        {
                            session.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Service shutting down", CancellationToken.None);
                        }
                        session.WebSocket.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error disposing WebSocket for session {SessionId}", session.SessionId);
                    }
                }
            }
            
            _sessions.Clear();
        }
    }
}