using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Configuration;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VoiceLiveApi
{
    internal class Program
    {
        private static string AZURE_AI_FOUNDRY_RESOURCE_NAME = string.Empty;
        private static string AZURE_AI_FOUNDRY_API_KEY = string.Empty;
        private static string API_VERSION = string.Empty;
        private static string MODEL_NAME = string.Empty;

        // Audio management
        private static List<byte> audioBuffer = new List<byte>();
        private static AudioConfig? audioConfig;
        private static PushAudioInputStream? pushStream;
        private static bool isListening = false;

        // Voice input components
        private static SpeechRecognizer? speechRecognizer;
        private static ClientWebSocket? currentWebSocket;
        private static bool isRecording = false;

        static async Task Main(string[] args)
        {
            // Initialize configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile("appsettings.Development.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            // Load configuration values
            AZURE_AI_FOUNDRY_RESOURCE_NAME = configuration["AzureAI:FoundryResourceName"] ?? throw new InvalidOperationException("AzureAI:FoundryResourceName not found in configuration");
            AZURE_AI_FOUNDRY_API_KEY = configuration["AzureAI:ApiKey"] ?? throw new InvalidOperationException("AzureAI:ApiKey not found in configuration");
            API_VERSION = configuration["AzureAI:ApiVersion"] ?? throw new InvalidOperationException("AzureAI:ApiVersion not found in configuration");
            MODEL_NAME = configuration["AzureAI:ModelName"] ?? throw new InvalidOperationException("AzureAI:ModelName not found in configuration");

            Console.WriteLine("Voice Live API Conversation Demo");
            Console.WriteLine("Press 'q' to quit, 's' to start/stop voice recording, or type messages");

            try
            {
                await StartVoiceLiveConversation();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        private static async Task StartVoiceLiveConversation()
        {
            // Build Voice Live API WebSocket URI
            var webSocketUri = BuildVoiceLiveWebSocketUri();
            Console.WriteLine($"Connecting to Voice Live API: {webSocketUri}");

            using var webSocket = new ClientWebSocket();
            currentWebSocket = webSocket;

            // Set authentication header
            webSocket.Options.SetRequestHeader("api-key", AZURE_AI_FOUNDRY_API_KEY);
            webSocket.Options.SetBuffer(8192, 8192); // Set buffer size for WebSocket
            webSocket.Options.HttpVersion = HttpVersion.Version11;
            webSocket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;

            // Connect to Voice Live API
            await webSocket.ConnectAsync(webSocketUri, CancellationToken.None);
            Console.WriteLine("Connected to Voice Live API");

            // Configure the session with Voice Live specific settings
            await ConfigureVoiceLiveSession(webSocket);

            // Initialize speech recognition for voice input
            await InitializeSpeechRecognition();

            // Start receiving messages and user input concurrently
            var receiveTask = ReceiveVoiceLiveMessages(webSocket);
            var inputTask = HandleUserInput(webSocket);

            await Task.WhenAny(receiveTask, inputTask);

            // Cleanup
            await CleanupSpeechRecognition();
        }

        private static async Task InitializeSpeechRecognition()
        {
            try
            {
                // Create speech config - you can use the same Azure key or a separate Speech Service key
                var speechConfig = SpeechConfig.FromSubscription(AZURE_AI_FOUNDRY_API_KEY, "eastus2"); // Replace with your region
                speechConfig.SpeechRecognitionLanguage = "en-US";

                // Create audio config for microphone input
                var audioConfig = AudioConfig.FromDefaultMicrophoneInput();

                // Create speech recognizer
                speechRecognizer = new SpeechRecognizer(speechConfig, audioConfig);

                // Configure event handlers
                speechRecognizer.Recognizing += OnRecognizing;
                speechRecognizer.Recognized += OnRecognized;
                speechRecognizer.Canceled += OnCanceled;

                Console.WriteLine("✓ Speech recognition initialized");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing speech recognition: {ex.Message}");
                Console.WriteLine("Voice input will not be available. You can still use text input.");
            }
        }

        private static async Task CleanupSpeechRecognition()
        {
            if (speechRecognizer != null)
            {
                if (isRecording)
                {
                    await speechRecognizer.StopContinuousRecognitionAsync();
                }
                speechRecognizer.Dispose();
            }
        }

        private static async void OnRecognizing(object sender, SpeechRecognitionEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Result.Text))
            {
                Console.Write($"\r🎤 Recognizing: {e.Result.Text}");
            }
        }

        private static async void OnRecognized(object sender, SpeechRecognitionEventArgs e)
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrEmpty(e.Result.Text))
            {
                Console.WriteLine($"\n🎤 Voice Input: {e.Result.Text}");

                if (currentWebSocket != null && currentWebSocket.State == WebSocketState.Open)
                {
                    await SendTextMessage(currentWebSocket, e.Result.Text);
                }
            }
            else if (e.Result.Reason == ResultReason.NoMatch)
            {
                Console.WriteLine("\n🎤 No speech could be recognized");
            }
        }

        private static void OnCanceled(object sender, SpeechRecognitionCanceledEventArgs e)
        {
            Console.WriteLine($"\n🎤 Recognition canceled: {e.Reason}");
            if (e.Reason == CancellationReason.Error)
            {
                Console.WriteLine($"Error: {e.ErrorDetails}");
            }
        }

        private static Uri BuildVoiceLiveWebSocketUri()
        {
            // Voice Live API WebSocket endpoint format
            var webSocketUrl = $"wss://{AZURE_AI_FOUNDRY_RESOURCE_NAME}.cognitiveservices.azure.com/voice-live/realtime?api-version={API_VERSION}&model={MODEL_NAME}";
            return new Uri(webSocketUrl);
        }

        private static async Task ConfigureVoiceLiveSession(ClientWebSocket webSocket)
        {
            // Configure session with Voice Live specific features
            var sessionConfig = new
            {
                type = "session.update",
                session = new
                {
                    modalities = new[] { "text", "audio" },
                    instructions = "You are a helpful AI assistant. Respond naturally and conversationally to the user's questions and requests. You will always respond with voice audio.",

                    // Voice Live specific voice configuration
                    voice = new
                    {
                        name = "en-US-Ava:DragonHDLatestNeural", // Azure Neural Voice
                        type = "azure-standard",
                        temperature = 0.6,
                        rate = "1.0"
                    },

                    // Audio format configuration
                    input_audio_format = "pcm16",
                    output_audio_format = "pcm16",

                    // Voice Live enhanced turn detection
                    turn_detection = new
                    {
                        type = "azure_semantic_vad", // Voice Live specific semantic VAD
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

                    // Audio processing enhancements
                    //audio_processing = new
                    //{
                    //    noise_suppression = true,
                    //    echo_cancellation = true
                    //},

                    // Model configuration
                    temperature = 0.6,
                    max_response_output_tokens = 12000,
                    tools = new object[] { },
                    tool_choice = "auto"
                }
            };

            await SendWebSocketMessage(webSocket, sessionConfig);
            //Console.WriteLine("Voice Live session configured with enhanced features");
            Console.WriteLine("Voice Live session configured");
        }

        private static async Task ReceiveVoiceLiveMessages(ClientWebSocket webSocket)
        {
            var buffer = new byte[8192];

            while (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    WebSocketReceiveResult result;
                    var wholeMessage = new StringBuilder();
                    do
                    {
                        result = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            CancellationToken.None);

                        var messageSegmentString = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        wholeMessage.Append(messageSegmentString);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        await HandleVoiceLiveMessage(webSocket, wholeMessage.ToString());
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine("Voice Live connection closed");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error receiving message: {ex.Message}");
                    break;
                }
            }
        }

        private static async Task HandleVoiceLiveMessage(ClientWebSocket webSocket, string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var messageType = doc.RootElement.GetProperty("type").GetString();

                switch (messageType)
                {
                    case "session.created":
                        Console.WriteLine("✓ Voice Live session created");
                        break;

                    case "session.updated":
                        Console.WriteLine("✓ Voice Live session updated - Ready for conversation!");
                        Console.WriteLine("You can now speak (press 's' to start/stop) or type messages...");
                        break;

                    case "conversation.item.created":
                        //Console.WriteLine("📝 Message added to conversation");
                        break;

                    case "response.created":
                        Console.WriteLine("🤖 AI is generating response...");
                        audioBuffer.Clear();
                        break;

                    case "response.content_part.added":
                        var root = doc.RootElement;
                        //Console.WriteLine($"📦 Content part added");
                        break;


                    case "response.audio.delta":
                        //Console.WriteLine("🎵 Audio delta received");
                        await HandleAudioDelta(doc.RootElement);
                        break;

                    case "response.audio.done":
                        Console.WriteLine("🎵 Audio response completed");
                        await PlayAggregatedAudio();
                        break;

                    case "response.text.delta":
                        HandleTextDelta(doc.RootElement);
                        break;

                    case "response.done":
                        //Console.WriteLine("\n💬 Response completed");
                        break;

                    case "input_audio_buffer.speech_started":
                        //Console.WriteLine("🎤 Speech detected...");
                        break;

                    case "input_audio_buffer.speech_stopped":
                        //Console.WriteLine("🔇 Speech ended");
                        break;

                    case "error":
                        var error = doc.RootElement.GetProperty("error");
                        Console.WriteLine($"❌ Error: {error}");
                        break;

                    default:
                        //Console.WriteLine($"📦 Received: {messageType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing message: {ex.Message}");
            }
        }

        private static async Task HandleAudioDelta(JsonElement messageElement)
        {
            try
            {
                if (messageElement.TryGetProperty("delta", out var deltaElement))// &&
                                                                                 //deltaElement.TryGetProperty("audio", out var audioElement))
                {
                    var base64Audio = deltaElement.GetString();
                    if (!string.IsNullOrEmpty(base64Audio))
                    {
                        var audioBytes = Convert.FromBase64String(base64Audio);
                        audioBuffer.AddRange(audioBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling audio delta: {ex.Message}");
            }
        }

        private static void HandleTextDelta(JsonElement messageElement)
        {
            try
            {
                if (messageElement.TryGetProperty("delta", out var deltaElement) &&
                    deltaElement.TryGetProperty("text", out var textElement))
                {
                    var text = textElement.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        Console.Write(text);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling text delta: {ex.Message}");
            }
        }

        private static async Task PlayAggregatedAudio()
        {
            try
            {
                if (audioBuffer.Count == 0) return;

                // Create WAV file from PCM16 data (Voice Live uses 24kHz sample rate)
                var wavData = CreateWavFile(audioBuffer.ToArray(), 24000, 1, 16);

                // Play audio
                var tempPath = Path.GetTempFileName();
                var wavPath = Path.ChangeExtension(tempPath, ".wav");
                await File.WriteAllBytesAsync(wavPath, wavData);

                //using var player = new SoundPlayer(wavPath);
                //player.PlaySync();

                File.Delete(wavPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error playing audio: {ex.Message}");
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

        private static async Task HandleUserInput(ClientWebSocket webSocket)
        {
            Console.WriteLine("Commands: 'q' to quit, 's' to start/stop voice recording, or type your message:");

            while (webSocket.State == WebSocketState.Open)
            {
                var input = Console.ReadLine();

                if (input?.ToLower() == "q")
                {
                    Console.WriteLine("Ending conversation...");
                    break;
                }
                else if (input?.ToLower() == "s")
                {
                    await ToggleVoiceRecording();
                }
                else if (!string.IsNullOrEmpty(input))
                {
                    await SendTextMessage(webSocket, input);
                }
            }
        }

        private static async Task ToggleVoiceRecording()
        {
            if (speechRecognizer == null)
            {
                Console.WriteLine("❌ Speech recognition not available");
                return;
            }

            try
            {
                if (!isRecording)
                {
                    await speechRecognizer.StartContinuousRecognitionAsync();
                    isRecording = true;
                    Console.WriteLine("🎤 Voice recording started - speak now!");
                }
                else
                {
                    await speechRecognizer.StopContinuousRecognitionAsync();
                    isRecording = false;
                    Console.WriteLine("🔇 Voice recording stopped");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error toggling voice recording: {ex.Message}");
                isRecording = false;
            }
        }

        private static async Task SendTextMessage(ClientWebSocket webSocket, string text)
        {
            try
            {
                // Add user message to conversation
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

                // Request response with both text and audio
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
                Console.WriteLine($"📤 Sent: {text}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending message: {ex.Message}");
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
    }
}