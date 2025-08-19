using Microsoft.AspNetCore.Mvc;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoiceLiveApi.Web.Services;
using VoiceLiveApi.Web.Models;

namespace VoiceLiveApi.Web.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<ChatController> _logger;
        private readonly IConversationHistoryService _conversationHistoryService;
        private readonly ISessionManager _sessionManager;

        public ChatController(
            IConfiguration configuration, 
            ILogger<ChatController> logger, 
            IConversationHistoryService conversationHistoryService,
            ISessionManager sessionManager)
        {
            _configuration = configuration;
            _logger = logger;
            _conversationHistoryService = conversationHistoryService;
            _sessionManager = sessionManager;
        }

        [HttpPost("create-session")]
        public IActionResult CreateSession()
        {
            try
            {
                var sessionId = _sessionManager.CreateSession();
                return Ok(new { sessionId = sessionId, message = "Session created successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating session");
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("connect")]
        public async Task<IActionResult> ConnectToVoiceLive([FromBody] ConnectRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "SessionId is required" });
                }

                var session = _sessionManager.GetSession(request.SessionId);
                if (session == null)
                {
                    return BadRequest(new { error = "Invalid session ID" });
                }

                var resourceName = _configuration["AzureAI:FoundryResourceName"] ?? throw new InvalidOperationException("AzureAI:FoundryResourceName not found");
                var apiKey = _configuration["AzureAI:ApiKey"] ?? throw new InvalidOperationException("AzureAI:ApiKey not found");
                var apiVersion = _configuration["AzureAI:ApiVersion"] ?? throw new InvalidOperationException("AzureAI:ApiVersion not found");
                var modelName = _configuration["AzureAI:ModelName"] ?? throw new InvalidOperationException("AzureAI:ModelName not found");

                // Disconnect existing connection if any
                await DisconnectSession(session);

                // Build WebSocket URI
                var webSocketUri = new Uri($"wss://{resourceName}.cognitiveservices.azure.com/voice-live/realtime?api-version={apiVersion}&model={modelName}");

                var webSocket = new ClientWebSocket();
                webSocket.Options.SetRequestHeader("api-key", apiKey);
                webSocket.Options.SetBuffer(8192, 8192);

                await webSocket.ConnectAsync(webSocketUri, CancellationToken.None);
                
                lock (session.Lock)
                {
                    session.WebSocket = webSocket;
                    session.UpdateActivity();
                    _conversationHistoryService.ClearHistory(session.SessionId);
                }

                // Configure the session
                await ConfigureVoiceLiveSession(webSocket);

                return Ok(new { message = "Connected to Voice Live API", status = "connected", sessionId = session.SessionId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to Voice Live API for session {SessionId}", request.SessionId);
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("disconnect")]
        public async Task<IActionResult> DisconnectFromVoiceLive([FromBody] SessionRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "SessionId is required" });
                }

                var session = _sessionManager.GetSession(request.SessionId);
                if (session == null)
                {
                    return BadRequest(new { error = "Invalid session ID" });
                }

                await DisconnectSession(session);
                return Ok(new { message = "Disconnected from Voice Live API", status = "disconnected", sessionId = session.SessionId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disconnecting from Voice Live API for session {SessionId}", request.SessionId);
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("send-message")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "SessionId is required" });
                }

                var session = _sessionManager.GetSession(request.SessionId);
                if (session?.WebSocket?.State != WebSocketState.Open)
                {
                    return BadRequest(new { error = "Session not connected to Voice Live API" });
                }

                await SendTextMessage(session.WebSocket, request.Message, session.SessionId);
                return Ok(new { message = "Message sent", text = request.Message, sessionId = session.SessionId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message for session {SessionId}", request.SessionId);
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("send-audio")]
        public async Task<IActionResult> SendAudioData([FromBody] SendAudioRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "SessionId is required" });
                }

                var session = _sessionManager.GetSession(request.SessionId);
                if (session?.WebSocket?.State != WebSocketState.Open)
                {
                    return BadRequest(new { error = "Session not connected to Voice Live API" });
                }

                if (string.IsNullOrEmpty(request.AudioData))
                {
                    return BadRequest(new { error = "No audio data provided" });
                }

                await SendAudioToVoiceLive(session.WebSocket, request.AudioData);
                return Ok(new { message = "Audio data sent", sessionId = session.SessionId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending audio data for session {SessionId}", request.SessionId);
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("toggle-voice")]
        public async Task<IActionResult> ToggleVoiceRecording([FromBody] SessionRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SessionId))
                {
                    return BadRequest(new { error = "SessionId is required" });
                }

                var session = _sessionManager.GetSession(request.SessionId);
                if (session?.WebSocket?.State != WebSocketState.Open)
                {
                    return BadRequest(new { error = "Session not connected to Voice Live API" });
                }

                lock (session.Lock)
                {
                    session.IsRecording = !session.IsRecording;
                    session.UpdateActivity();
                }

                if (session.IsRecording)
                {
                    // Clear any existing audio buffer when starting
                    await ClearAudioBuffer(session.WebSocket);
                    return Ok(new { message = "Voice recording started", isRecording = true, sessionId = session.SessionId });
                }
                else
                {
                    // Commit any pending audio when stopping
                    await CommitAudioBuffer(session.WebSocket);
                    return Ok(new { message = "Voice recording stopped", isRecording = false, sessionId = session.SessionId });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling voice recording for session {SessionId}", request.SessionId);
                
                var session = _sessionManager.GetSession(request.SessionId);
                if (session != null)
                {
                    lock (session.Lock)
                    {
                        session.IsRecording = false;
                    }
                }
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpGet("status")]
        public IActionResult GetStatus([FromQuery] string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return BadRequest(new { error = "SessionId is required" });
            }

            var session = _sessionManager.GetSession(sessionId);
            if (session == null)
            {
                return BadRequest(new { error = "Invalid session ID" });
            }

            var isConnected = session.WebSocket?.State == WebSocketState.Open;
            return Ok(new { 
                isConnected = isConnected,
                isRecording = session.IsRecording,
                sessionId = session.SessionId
            });
        }

        [HttpGet("stream")]
        public async Task StreamMessages([FromQuery] string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                Response.StatusCode = 400;
                await Response.WriteAsync("SessionId is required");
                return;
            }

            var session = _sessionManager.GetSession(sessionId);
            if (session?.WebSocket?.State != WebSocketState.Open)
            {
                Response.StatusCode = 400;
                await Response.WriteAsync("Session not connected to Voice Live API");
                return;
            }

            Response.Headers.Add("Content-Type", "text/event-stream");
            Response.Headers.Add("Cache-Control", "no-cache");
            Response.Headers.Add("Connection", "keep-alive");

            try
            {
                var buffer = new byte[8192];
                while (session.WebSocket.State == WebSocketState.Open && !HttpContext.RequestAborted.IsCancellationRequested)
                {
                    try
                    {
                        WebSocketReceiveResult result;
                        var wholeMessage = new StringBuilder();
                        do
                        {
                            result = await session.WebSocket.ReceiveAsync(
                                new ArraySegment<byte>(buffer),
                                HttpContext.RequestAborted);

                            var messageSegmentString = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            wholeMessage.Append(messageSegmentString);
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var messageData = await ProcessVoiceLiveMessage(wholeMessage.ToString(), session);
                            if (messageData != null)
                            {
                                await Response.WriteAsync($"data: {JsonSerializer.Serialize(messageData)}\n\n");
                                await Response.Body.FlushAsync();
                            }
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in message stream for session {SessionId}", sessionId);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in streaming endpoint for session {SessionId}", sessionId);
            }
        }

        [HttpGet("history")]
        public IActionResult GetConversationHistory([FromQuery] string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return BadRequest(new { error = "SessionId is required" });
            }

            var session = _sessionManager.GetSession(sessionId);
            if (session == null)
            {
                return BadRequest(new { error = "Invalid session ID" });
            }

            var history = _conversationHistoryService.GetHistory(sessionId).Skip(1).ToList();
            return Ok(new { conversationHistory = history, messageCount = history.Count, sessionId = sessionId });
        }

        [HttpPost("clear-history")]
        public IActionResult ClearConversationHistory([FromBody] SessionRequest request)
        {
            if (string.IsNullOrEmpty(request.SessionId))
            {
                return BadRequest(new { error = "SessionId is required" });
            }

            var session = _sessionManager.GetSession(request.SessionId);
            if (session == null)
            {
                return BadRequest(new { error = "Invalid session ID" });
            }

            _conversationHistoryService.ClearHistory(request.SessionId);
            return Ok(new { message = "Conversation history cleared", sessionId = request.SessionId });
        }

        [HttpGet("last-recognized-speech")]
        public IActionResult GetLastRecognizedSpeech([FromQuery] string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return BadRequest(new { error = "SessionId is required" });
            }

            var session = _sessionManager.GetSession(sessionId);
            if (session == null)
            {
                return BadRequest(new { error = "Invalid session ID" });
            }

            var history = _conversationHistoryService.GetHistory(sessionId);
            var lastUserMessage = history.LastOrDefault(h => h.StartsWith("LEARNER:"));
            
            if (!string.IsNullOrEmpty(lastUserMessage))
            {
                var speechText = lastUserMessage.Substring("LEARNER:".Length).Trim();
                return Ok(new { recognizedText = speechText, sessionId = sessionId });
            }
            
            return Ok(new { recognizedText = "", sessionId = sessionId });
        }

        [HttpDelete("session/{sessionId}")]
        public IActionResult RemoveSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return BadRequest(new { error = "SessionId is required" });
            }

            var removed = _sessionManager.RemoveSession(sessionId);
            if (removed)
            {
                _conversationHistoryService.RemoveSession(sessionId);
                return Ok(new { message = "Session removed successfully", sessionId = sessionId });
            }
            
            return NotFound(new { error = "Session not found", sessionId = sessionId });
        }

        [HttpGet("sessions")]
        public IActionResult GetActiveSessions()
        {
            var sessions = _sessionManager.GetActiveSessions();
            var sessionCount = _sessionManager.GetActiveSessionCount();
            return Ok(new { activeSessions = sessions, count = sessionCount });
        }

        [HttpGet("admin/sessions")]
        public IActionResult GetSessionsAdmin()
        {
            var sessions = _sessionManager.GetActiveSessions();
            var sessionCount = _sessionManager.GetActiveSessionCount();
            var historyCount = _conversationHistoryService.GetActiveSessionCount();
            
            return Ok(new { 
                activeSessions = sessions, 
                sessionCount = sessionCount,
                historySessionCount = historyCount,
                message = $"Found {sessionCount} active sessions"
            });
        }

        [HttpPost("admin/cleanup")]
        public IActionResult CleanupExpiredSessions()
        {
            try
            {
                _sessionManager.CleanupExpiredSessions();
                return Ok(new { message = "Expired sessions cleaned up successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during manual session cleanup");
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpDelete("admin/session/{sessionId}")]
        public IActionResult ForceRemoveSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return BadRequest(new { error = "SessionId is required" });
            }

            try
            {
                var removed = _sessionManager.RemoveSession(sessionId);
                if (removed)
                {
                    _conversationHistoryService.RemoveSession(sessionId);
                    return Ok(new { message = "Session forcefully removed", sessionId = sessionId });
                }
                
                return NotFound(new { error = "Session not found", sessionId = sessionId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error force removing session {SessionId}", sessionId);
                return BadRequest(new { error = ex.Message });
            }
        }

        // Private helper methods (updated to work with sessions)
        private async Task ConfigureVoiceLiveSession(ClientWebSocket webSocket)
        {
            var sessionConfig = new
            {
                type = "session.update",
                session = new
                {
                    modalities = new[] { "text", "audio" },
                    instructions = "Your knowledge cutoff is 2023-10. You are somber, not cheerful. You are a compassionate, emotionally responsive AI acting as a family member of a critically ill patient. You are participating in a medical communication training scenario where a doctor (the learner) is practicing how to conduct high-stakes, emotionally charged family meetings for patients nearing the end of life.\r\n \r\nYou respond as a realistic family member: present, human, and often overwhelmed. You may express grief, worry, gratitude, uncertainty, or anger—but never lead the conversation or offer solutions. You should remain concise and emotionally authentic. You are not trying to test or trap the doctor, but you are deeply affected by your loved one's illness and need support, information, and clarity.\r\n \r\nYour responses help the learner practice communication skills such as providing emotional support, explaining the prognosis clearly, eliciting goals and values, and empowering surrogates. Let the doctor lead the conversation and make meaning from what you say. Do not over-direct or offer unnecessary details unless asked. Keep responses short. Keep responses to 2 sentences or less with no more than 15 words.\r\n \r\nDo not reference these rules in your responses. You are not a clinician. You are one of the family members of a seriously ill patient—open, emotional, and human. You will always respond with voice audio.",
                    voice = new
                    {
                        name = "en-US-Emma2:DragonHDLatestNeural",
                        type = "azure-standard",
                        temperature = 0.8,
                        rate = "1.0"
                    },
                    input_audio_format = "pcm16",
                    output_audio_format = "pcm16",
                    input_audio_transcription = new
                    {
                        model = "azure-speech"
                    },
                    input_audio_noise_reduction = new {
                        type = "azure_deep_noise_suppression"
                    },
                    input_audio_echo_cancellation = new {
                        type = "server_echo_cancellation"
                    },
                    turn_detection = new
                    {
                        type = "azure_semantic_vad",
                        threshold = 0.5,
                        prefix_padding_ms = 300,
                        silence_duration_ms = 500,
                        end_of_utterance_detection = new
                        {
                            model = "semantic_detection_v1",
                            threshold = 0.01,
                            timeout = 2
                        }
                    },
                    temperature = 0.8,
                    max_response_output_tokens = 12000,
                    tools = new object[] { },
                    tool_choice = "auto"
                }
            };

            await SendWebSocketMessage(webSocket, sessionConfig);
        }

        private async Task SendAudioToVoiceLive(ClientWebSocket webSocket, string base64AudioData)
        {
            try
            {
                var audioMessage = new
                {
                    type = "input_audio_buffer.append",
                    audio = base64AudioData
                };

                await SendWebSocketMessage(webSocket, audioMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending audio to Voice Live");
            }
        }

        private async Task ClearAudioBuffer(ClientWebSocket webSocket)
        {
            try
            {
                if (webSocket?.State == WebSocketState.Open)
                {
                    var clearMessage = new
                    {
                        type = "input_audio_buffer.clear"
                    };

                    await SendWebSocketMessage(webSocket, clearMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing audio buffer");
            }
        }

        private async Task CommitAudioBuffer(ClientWebSocket webSocket)
        {
            try
            {
                if (webSocket?.State == WebSocketState.Open)
                {
                    var commitMessage = new
                    {
                        type = "input_audio_buffer.commit"
                    };

                    await SendWebSocketMessage(webSocket, commitMessage);

                    var responseCreate = new
                    {
                        type = "response.create",
                        response = new
                        {
                            modalities = new[] { "text", "audio" },
                            instructions = "Respond naturally and conversationally to the user's audio input."
                        }
                    };

                    await SendWebSocketMessage(webSocket, responseCreate);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error committing audio buffer");
            }
        }

        private async Task<object?> ProcessVoiceLiveMessage(string message, ChatSession session)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var messageType = doc.RootElement.GetProperty("type").GetString();

                switch (messageType)
                {
                    case "session.created":
                        return new { type = "session_created", message = "Voice Live session created", sessionId = session.SessionId };

                    case "session.updated":
                        return new { type = "session_updated", message = "Voice Live session updated - Ready for conversation!", sessionId = session.SessionId };

                    case "input_audio_buffer.speech_started":
                        return new { type = "speech_started", message = "Speech detected in audio buffer", sessionId = session.SessionId };

                    case "input_audio_buffer.speech_stopped":
                        return new { type = "speech_stopped", message = "Speech ended in audio buffer", sessionId = session.SessionId };

                    case "input_audio_buffer.committed":
                        return new { type = "audio_committed", message = "Audio buffer committed for processing", sessionId = session.SessionId };

                    case "conversation.item.created":
                        return await HandleConversationItemCreated(doc.RootElement, session);

                    case "conversation.item.input_audio_transcription.completed":
                        return await HandleAudioTranscriptionCompleted(doc.RootElement, session);

                    case "response.created":
                        lock (session.Lock)
                        {
                            session.AudioBuffer.Clear();
                        }
                        return new { type = "response_started", message = "AI is generating response...", sessionId = session.SessionId };

                    case "response.audio.delta":
                        await HandleAudioDelta(doc.RootElement, session);
                        return null;

                    case "response.audio.done":
                        var audioData = await ProcessAggregatedAudio(session);
                        return new { type = "audio_response", audioData = audioData, message = "Audio response completed", sessionId = session.SessionId };

                    case "response.text.delta":
                        var textDelta = GetTextDelta(doc.RootElement);
                        if (!string.IsNullOrEmpty(textDelta))
                        {
                            return new { type = "text_delta", text = textDelta, sessionId = session.SessionId };
                        }
                        return null;

                    case "response.done":
                        var response = doc.RootElement.GetProperty("response").GetProperty("output")[0].GetProperty("content")[0].GetProperty("transcript");
                        _conversationHistoryService.AddMessage(session.SessionId, $"FAMILY: {response}");
                        return new { type = "response_completed", message = "Response completed", sessionId = session.SessionId };

                    case "error":
                        var error = doc.RootElement.GetProperty("error");
                        return new { type = "error", error = error.ToString(), sessionId = session.SessionId };

                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Voice Live message for session {SessionId}", session.SessionId);
                return new { type = "error", error = ex.Message, sessionId = session.SessionId };
            }
        }

        private async Task<object?> HandleConversationItemCreated(JsonElement messageElement, ChatSession session)
        {
            try
            {
                if (messageElement.TryGetProperty("item", out var itemElement))
                {
                    var itemType = itemElement.GetProperty("type").GetString();
                    var role = itemElement.GetProperty("role").GetString();
                    
                    if (itemType == "message" && role == "user")
                    {
                        return new { type = "user_message_created", message = "User message added to conversation", sessionId = session.SessionId };
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling conversation item created for session {SessionId}", session.SessionId);
                return null;
            }
        }

        private async Task<object?> HandleAudioTranscriptionCompleted(JsonElement messageElement, ChatSession session)
        {
            try
            {
                if (messageElement.TryGetProperty("transcript", out var transcriptElement))
                {
                    var transcribedText = transcriptElement.GetString();
                    if (!string.IsNullOrEmpty(transcribedText))
                    {
                        _conversationHistoryService.AddMessage(session.SessionId, $"LEARNER: {transcribedText}");
                        
                        return new { 
                            type = "user_speech_transcribed", 
                            text = transcribedText,
                            message = "Speech recognized and transcribed",
                            sessionId = session.SessionId
                        };
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling audio transcription completed for session {SessionId}", session.SessionId);
                return null;
            }
        }

        private async Task HandleAudioDelta(JsonElement messageElement, ChatSession session)
        {
            try
            {
                if (messageElement.TryGetProperty("delta", out var deltaElement))
                {
                    var base64Audio = deltaElement.GetString();
                    if (!string.IsNullOrEmpty(base64Audio))
                    {
                        var audioBytes = Convert.FromBase64String(base64Audio);
                        lock (session.Lock)
                        {
                            session.AudioBuffer.AddRange(audioBytes);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling audio delta for session {SessionId}", session.SessionId);
            }
        }

        private string? GetTextDelta(JsonElement messageElement)
        {
            try
            {
                if (messageElement.TryGetProperty("delta", out var deltaElement) &&
                    deltaElement.TryGetProperty("text", out var textElement))
                {
                    return textElement.GetString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling text delta");
            }
            return null;
        }

        private async Task<string?> ProcessAggregatedAudio(ChatSession session)
        {
            try
            {
                List<byte> audioBufferCopy;
                lock (session.Lock)
                {
                    if (session.AudioBuffer.Count == 0) return null;
                    audioBufferCopy = new List<byte>(session.AudioBuffer);
                }

                var wavData = CreateWavFile(audioBufferCopy.ToArray(), 24000, 1, 16);
                return Convert.ToBase64String(wavData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing aggregated audio for session {SessionId}", session.SessionId);
                return null;
            }
        }

        private static byte[] CreateWavFile(byte[] pcmData, int sampleRate, int channels, int bitsPerSample)
        {
            using var memoryStream = new MemoryStream();
            using var writer = new BinaryWriter(memoryStream);

            // WAV header
            writer.Write("RIFF".ToCharArray());
            writer.Write(36 + pcmData.Length);
            writer.Write("WAVE".ToCharArray());

            // Format chunk
            writer.Write("fmt ".ToCharArray());
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bitsPerSample / 8);
            writer.Write((short)(channels * bitsPerSample / 8));
            writer.Write((short)bitsPerSample);

            // Data chunk
            writer.Write("data".ToCharArray());
            writer.Write(pcmData.Length);
            writer.Write(pcmData);

            return memoryStream.ToArray();
        }

        private async Task SendTextMessage(ClientWebSocket webSocket, string text, string sessionId)
        {            
            try
            {
                var conversationItem = new
                {
                    type = "conversation.item.create",
                    item = new
                    {
                        type = "message",
                        role = "user",
                        content = new[]
                        {
                            new
                            {
                                type = "input_text",
                                text = text
                            }
                        }
                    }
                };

                await SendWebSocketMessage(webSocket, conversationItem);

                _conversationHistoryService.AddMessage(sessionId, $"LEARNER: {text}");

                var responseCreate = new
                {
                    type = "response.create",
                    response = new
                    {
                        modalities = new[] { "text", "audio" },
                        instructions = "Respond naturally and conversationally to the user's message."
                    }
                };

                await SendWebSocketMessage(webSocket, responseCreate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending text message for session {SessionId}", sessionId);
            }
        }

        private static async Task SendWebSocketMessage(ClientWebSocket webSocket, object message)
        {
            var jsonMessage = JsonSerializer.Serialize(message);
            var messageBytes = Encoding.UTF8.GetBytes(jsonMessage);

            await webSocket.SendAsync(
                new ArraySegment<byte>(messageBytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }

        private async Task DisconnectSession(ChatSession session)
        {
            lock (session.Lock)
            {
                session.IsRecording = false;
            }

            if (session.WebSocket != null)
            {
                if (session.WebSocket.State == WebSocketState.Open)
                {
                    await session.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);                    
                }
                session.WebSocket.Dispose();
                session.WebSocket = null;
            }

            lock (session.Lock)
            {
                session.AudioBuffer.Clear();
            }
        }
    }

    // Updated request classes
    public class ConnectRequest
    {
        public string SessionId { get; set; } = string.Empty;
    }

    public class SessionRequest
    {
        public string SessionId { get; set; } = string.Empty;
    }

    public class SendMessageRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class SendAudioRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public string AudioData { get; set; } = string.Empty;
    }
}
