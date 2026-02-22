using System.Collections.Concurrent;
using System.IO;
using System.Net.Http.Json;
using System.Net.Sockets;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Transport;

namespace ZCL.Services.LLM;

public sealed class LLMChatService : IZcspService
{
    public string ServiceName => "LLMChat";

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<Guid, NetworkStream> _sessions = new();

    public event Func<string, Task>? ResponseReceived;

    public LLMChatService()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:11434"),
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public Task OnSessionStartedAsync(Guid sessionId, string remotePeerId, NetworkStream stream)
    {
        _sessions[sessionId] = stream;
        return Task.CompletedTask;
    }

    public async Task OnSessionDataAsync(Guid sessionId, BinaryReader reader)
    {
 

        var action = BinaryCodec.ReadString(reader);

        switch (action)
        {
            case "AiQuery":
                var prompt = BinaryCodec.ReadString(reader);
                await HandleAiQueryAsync(sessionId, prompt);
                break;

            case "AiResponse":
                {
                    var response = BinaryCodec.ReadString(reader);

                    if (ResponseReceived != null)
                    {
                        var handlers = ResponseReceived.GetInvocationList();

                        foreach (Func<string, Task> handler in handlers)
                            await handler(response);
                    }


                    break;
                }
        }
    }

    public Task OnSessionClosedAsync(Guid sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    // =========================
    // CLIENT SIDE
    // =========================

    public async Task SendQueryAsync(Guid sessionId, string prompt)
    {
        if (!_sessions.TryGetValue(sessionId, out var stream))
            throw new InvalidOperationException("AI session not active.");

        var msg = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            sessionId,
            w =>
            {
                BinaryCodec.WriteString(w, "AiQuery");
                BinaryCodec.WriteString(w, prompt);
            });

        await Framing.WriteAsync(stream, msg);
    }

    // =========================
    // HOST SIDE
    // =========================

    private async Task HandleAiQueryAsync(Guid sessionId, string prompt)
    {
        try
        {
            if (!_sessions.TryGetValue(sessionId, out var stream))
                return;

            if (prompt.Length > 4000)
                prompt = prompt[..4000];

            var reply = await GenerateLocalAsync(prompt);

            var responseMsg = BinaryCodec.Serialize(
                ZcspMessageType.SessionData,
                sessionId,
                w =>
                {
                    BinaryCodec.WriteString(w, "AiResponse");
                    BinaryCodec.WriteString(w, reply);
                });

            await Framing.WriteAsync(stream, responseMsg);
        }
        catch (TaskCanceledException)
        {
            // timeout or disconnect
        }
        catch (IOException)
        {
            // stream closed mid-send
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AI error: {ex.Message}");
        }
    }

    // =========================
    // LOCAL OLLAMA CALL
    // =========================

    public async Task<string> GenerateLocalAsync(string prompt)
    {
        try
        {
            var httpResponse = await _http.PostAsJsonAsync(
                "/api/generate",
                new
                {
                    model = "phi3:latest",
                    prompt = prompt,
                    stream = false
                });

            httpResponse.EnsureSuccessStatusCode();

            var result = await httpResponse.Content
                .ReadFromJsonAsync<OllamaResponse>();

            return result?.Response?.Trim() ?? "No response.";
        }
        catch (HttpRequestException)
        {
            return "AI service unavailable on this peer.";
        }
    }


    private sealed class OllamaResponse
    {
        public string Response { get; set; } = "";
    }
}
