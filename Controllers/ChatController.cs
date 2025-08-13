using Microsoft.AspNetCore.Mvc;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VoiceLiveApi.Web.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<ChatController> _logger;
        
        // Single-user session state
        private static ClientWebSocket? _currentWebSocket;
        private static List<byte> _audioBuffer = new List<byte>();
        private static SpeechRecognizer? _speechRecognizer;
        private static bool _isRecording = false;
        private static readonly object _lock = new object();

        public ChatController(IConfiguration configuration, ILogger<ChatController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        [HttpPost("connect")]
        public async Task<IActionResult> ConnectToVoiceLive()
        {
            try
            {
                var resourceName = _configuration["AzureAI:FoundryResourceName"] ?? throw new InvalidOperationException("AzureAI:FoundryResourceName not found");
                var apiKey = _configuration["AzureAI:ApiKey"] ?? throw new InvalidOperationException("AzureAI:ApiKey not found");
                var apiVersion = _configuration["AzureAI:ApiVersion"] ?? throw new InvalidOperationException("AzureAI:ApiVersion not found");
                var modelName = _configuration["AzureAI:ModelName"] ?? throw new InvalidOperationException("AzureAI:ModelName not found");

                // Disconnect existing connection if any
                await DisconnectFromVoiceLive();

                // Build WebSocket URI
                var webSocketUri = new Uri($"wss://{resourceName}.cognitiveservices.azure.com/voice-live/realtime?api-version={apiVersion}&model={modelName}");

                var webSocket = new ClientWebSocket();
                webSocket.Options.SetRequestHeader("api-key", apiKey);
                webSocket.Options.SetBuffer(8192, 8192);

                await webSocket.ConnectAsync(webSocketUri, CancellationToken.None);
                
                lock (_lock)
                {
                    _currentWebSocket = webSocket;
                }

                // Configure the session
                await ConfigureVoiceLiveSession(webSocket);

                // Initialize speech recognition
                await InitializeSpeechRecognition(apiKey);

                return Ok(new { message = "Connected to Voice Live API", status = "connected" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to Voice Live API");
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("disconnect")]
        public async Task<IActionResult> DisconnectFromVoiceLive()
        {
            try
            {
                await CleanupResources();
                return Ok(new { message = "Disconnected from Voice Live API", status = "disconnected" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disconnecting from Voice Live API");
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("send-message")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
        {
            try
            {
                if (_currentWebSocket?.State != WebSocketState.Open)
                {
                    return BadRequest(new { error = "Not connected to Voice Live API" });
                }

                await SendTextMessage(_currentWebSocket, request.Message);
                return Ok(new { message = "Message sent", text = request.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message");
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("toggle-voice")]
        public async Task<IActionResult> ToggleVoiceRecording()
        {
            try
            {
                if (_speechRecognizer == null)
                {
                    return BadRequest(new { error = "Speech recognition not available" });
                }

                if (!_isRecording)
                {
                    await _speechRecognizer.StartContinuousRecognitionAsync();
                    _isRecording = true;
                    return Ok(new { message = "Voice recording started", isRecording = true });
                }
                else
                {
                    await _speechRecognizer.StopContinuousRecognitionAsync();
                    _isRecording = false;
                    return Ok(new { message = "Voice recording stopped", isRecording = false });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling voice recording");
                _isRecording = false;
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            var isConnected = _currentWebSocket?.State == WebSocketState.Open;
            return Ok(new { 
                isConnected = isConnected,
                isRecording = _isRecording,
                hasSpeechRecognizer = _speechRecognizer != null
            });
        }

        [HttpGet("stream")]
        public async Task StreamMessages()
        {
            if (_currentWebSocket?.State != WebSocketState.Open)
            {
                Response.StatusCode = 400;
                await Response.WriteAsync("Not connected to Voice Live API");
                return;
            }

            Response.Headers.Add("Content-Type", "text/event-stream");
            Response.Headers.Add("Cache-Control", "no-cache");
            Response.Headers.Add("Connection", "keep-alive");

            try
            {
                var buffer = new byte[8192];
                while (_currentWebSocket.State == WebSocketState.Open && !HttpContext.RequestAborted.IsCancellationRequested)
                {
                    try
                    {
                        WebSocketReceiveResult result;
                        var wholeMessage = new StringBuilder();
                        do
                        {
                            result = await _currentWebSocket.ReceiveAsync(
                                new ArraySegment<byte>(buffer),
                                HttpContext.RequestAborted);

                            var messageSegmentString = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            wholeMessage.Append(messageSegmentString);
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var messageData = await ProcessVoiceLiveMessage(wholeMessage.ToString());
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
                        _logger.LogError(ex, "Error in message stream");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in streaming endpoint");
            }
        }

        private async Task ConfigureVoiceLiveSession(ClientWebSocket webSocket)
        {
            var sessionConfig = new
            {
                type = "session.update",
                session = new
                {
                    modalities = new[] { "text", "audio" },
                    instructions = "Your knowledge cutoff is 2023-10. You are somber, not cheerful. You are a compassionate, emotionally responsive AI acting as a family member of a critically ill patient. You are participating in a medical communication training scenario where a doctor (the learner) is practicing how to conduct high-stakes, emotionally charged family meetings for patients nearing the end of life.\r\n \r\nYou respond as a realistic family member: present, human, and often overwhelmed. You may express grief, worry, gratitude, uncertainty, or anger—but never lead the conversation or offer solutions. You should remain concise and emotionally authentic. You are not trying to test or trap the doctor, but you are deeply affected by your loved one’s illness and need support, information, and clarity.\r\n \r\nYour responses help the learner practice communication skills such as providing emotional support, explaining the prognosis clearly, eliciting goals and values, and empowering surrogates. Let the doctor lead the conversation and make meaning from what you say. Do not over-direct or offer unnecessary details unless asked. Keep responses short. Keep responses to 2 sentences or less with no more than 15 words.\r\n \r\nDo not reference these rules in your responses. You are not a clinician. You are one of the family members of a seriously ill patient—open, emotional, and human. You will always respond with voice audio.",
                    voice = new
                    {
                        name = "en-US-Emma2:DragonHDLatestNeural",
                        type = "azure-standard",
                        temperature = 0.8,
                        rate = "1.0"
                    },
                    input_audio_format = "pcm16",
                    output_audio_format = "pcm16",
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

        private async Task InitializeSpeechRecognition(string apiKey)
        {
            try
            {
                var speechConfig = SpeechConfig.FromSubscription(apiKey, "eastus2");
                speechConfig.SpeechRecognitionLanguage = "en-US";
                var audioConfig = AudioConfig.FromDefaultMicrophoneInput();

                _speechRecognizer = new SpeechRecognizer(speechConfig, audioConfig);

                _speechRecognizer.Recognized += async (sender, e) =>
                {
                    if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrEmpty(e.Result.Text))
                    {
                        if (_currentWebSocket?.State == WebSocketState.Open)
                        {
                            await SendTextMessage(_currentWebSocket, e.Result.Text);
                        }
                    }
                };

                _speechRecognizer.Canceled += (sender, e) =>
                {
                    _logger.LogWarning($"Speech recognition canceled: {e.Reason}");
                    if (e.Reason == CancellationReason.Error)
                    {
                        _logger.LogError($"Speech recognition error: {e.ErrorDetails}");
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing speech recognition");
            }
        }

        private async Task<object?> ProcessVoiceLiveMessage(string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var messageType = doc.RootElement.GetProperty("type").GetString();

                switch (messageType)
                {
                    case "session.created":
                        return new { type = "session_created", message = "Voice Live session created" };

                    case "session.updated":
                        return new { type = "session_updated", message = "Voice Live session updated - Ready for conversation!" };

                    case "response.created":
                        _audioBuffer.Clear();
                        return new { type = "response_started", message = "AI is generating response..." };

                    case "response.audio.delta":
                        await HandleAudioDelta(doc.RootElement);
                        return null;

                    case "response.audio.done":
                        var audioData = await ProcessAggregatedAudio();
                        return new { type = "audio_response", audioData = audioData, message = "Audio response completed" };

                    case "response.text.delta":
                        var textDelta = GetTextDelta(doc.RootElement);
                        if (!string.IsNullOrEmpty(textDelta))
                        {
                            return new { type = "text_delta", text = textDelta };
                        }
                        return null;

                    case "response.done":
                        return new { type = "response_completed", message = "Response completed" };

                    case "error":
                        var error = doc.RootElement.GetProperty("error");
                        return new { type = "error", error = error.ToString() };

                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Voice Live message");
                return new { type = "error", error = ex.Message };
            }
        }

        private async Task HandleAudioDelta(JsonElement messageElement)
        {
            try
            {
                if (messageElement.TryGetProperty("delta", out var deltaElement))
                {
                    var base64Audio = deltaElement.GetString();
                    if (!string.IsNullOrEmpty(base64Audio))
                    {
                        var audioBytes = Convert.FromBase64String(base64Audio);
                        _audioBuffer.AddRange(audioBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling audio delta");
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

        private async Task<string?> ProcessAggregatedAudio()
        {
            try
            {
                if (_audioBuffer.Count == 0) return null;

                var wavData = CreateWavFile(_audioBuffer.ToArray(), 24000, 1, 16);
                return Convert.ToBase64String(wavData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing aggregated audio");
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

        private async Task SendTextMessage(ClientWebSocket webSocket, string text)
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
                _logger.LogError(ex, "Error sending text message");
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

        private async Task CleanupResources()
        {
            if (_speechRecognizer != null)
            {
                if (_isRecording)
                {
                    await _speechRecognizer.StopContinuousRecognitionAsync();
                    _isRecording = false;
                }
                _speechRecognizer.Dispose();
                _speechRecognizer = null;
            }

            if (_currentWebSocket != null)
            {
                if (_currentWebSocket.State == WebSocketState.Open)
                {
                    await _currentWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                }
                _currentWebSocket.Dispose();
                _currentWebSocket = null;
            }

            _audioBuffer.Clear();
        }
    }

    public class SendMessageRequest
    {
        public string Message { get; set; } = string.Empty;
    }
}
