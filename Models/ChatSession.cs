using System.Net.WebSockets;

namespace VoiceLiveApi.Web.Models
{
    public class ChatSession
    {
        public string SessionId { get; set; } = string.Empty;
        public ClientWebSocket? WebSocket { get; set; }
        public List<byte> AudioBuffer { get; set; } = new List<byte>();
        public bool IsRecording { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public readonly object Lock = new object();

        public void UpdateActivity()
        {
            LastActivity = DateTime.UtcNow;
        }

        public bool IsExpired(TimeSpan timeout)
        {
            return DateTime.UtcNow - LastActivity > timeout;
        }
    }
}