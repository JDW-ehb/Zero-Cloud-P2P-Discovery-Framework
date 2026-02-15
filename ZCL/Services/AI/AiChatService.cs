using System.IO;
using System.Net.Http.Json;
using System.Net.Sockets;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Transport;

namespace ZCL.Services.AI;

public sealed class AiChatService : IZcspService
{
    public string ServiceName => "AIChat";

    private readonly HttpClient _http;
    private NetworkStream? _stream;
    private Guid _currentSessionId;

    public event Action<string>? ResponseReceived;
    public event Action<string>? SummaryReceived;

    public AiChatService()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:11434"),
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public void BindStream(NetworkStream stream)
    {
        _stream = stream;
    }

    public Task OnSessionStartedAsync(Guid sessionId, string remotePeerId)
    {
        _currentSessionId = sessionId;
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
                var response = BinaryCodec.ReadString(reader);
                ResponseReceived?.Invoke(response);
                break;

            case "AiSummary":
                var summary = BinaryCodec.ReadString(reader);
                SummaryReceived?.Invoke(summary);
                break;
        }
    }

    public Task OnSessionClosedAsync(Guid sessionId)
    {
        _stream = null;
        _currentSessionId = Guid.Empty;
        return Task.CompletedTask;
    }

    // =========================
    // CLIENT SIDE
    // =========================

    public async Task SendQueryAsync(string prompt)
    {
        if (_stream == null)
            throw new InvalidOperationException("AI session not active.");

        var msg = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            _currentSessionId,
            w =>
            {
                BinaryCodec.WriteString(w, "AiQuery");
                BinaryCodec.WriteString(w, prompt);
            });

        await Framing.WriteAsync(_stream, msg);
    }

    // =========================
    // HOST SIDE
    // =========================

    private async Task HandleAiQueryAsync(Guid sessionId, string prompt)
    {
        try
        {
            if (_stream == null)
                return;

            if (prompt.Length > 4000)
                prompt = prompt[..4000];

            // ---- MAIN AI REPLY ----
            var reply = await GenerateLocalAsync(prompt);

            if (_stream == null)
                return;

            var responseMsg = BinaryCodec.Serialize(
                ZcspMessageType.SessionData,
                sessionId,
                w =>
                {
                    BinaryCodec.WriteString(w, "AiResponse");
                    BinaryCodec.WriteString(w, reply);
                });

            await Framing.WriteAsync(_stream, responseMsg);

            // ---- SUMMARY GENERATION ----
            var summaryPrompt =
                $"Summarize this conversation in one short title (max 6 words):\n\nUser: {prompt}\nAI: {reply}";

            var summary = await GenerateLocalAsync(summaryPrompt);

            if (_stream == null)
                return;

            var summaryMsg = BinaryCodec.Serialize(
                ZcspMessageType.SessionData,
                sessionId,
                w =>
                {
                    BinaryCodec.WriteString(w, "AiSummary");
                    BinaryCodec.WriteString(w, summary.Trim());
                });

            await Framing.WriteAsync(_stream, summaryMsg);
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

    private async Task<string> GenerateLocalAsync(string prompt)
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

    private sealed class OllamaResponse
    {
        public string Response { get; set; } = "";
    }
}
