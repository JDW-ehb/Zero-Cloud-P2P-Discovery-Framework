using System.IO;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Transport;

namespace ZCL.Services.LLM;

public sealed class LLMChatService : IZcspService
{
    public string ServiceName => "LLMChat";

    private readonly ZcspPeer _peer;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private NetworkStream? _stream;
    private Guid _currentSessionId;
    private string? _remotePeerId;

    public event Func<string, Task>? ResponseReceived;

    public LLMChatService(ZcspPeer peer)
    {
        _peer = peer;

        _http = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:11434"),
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    // =====================================================
    // SESSION MANAGEMENT (Transport-Agnostic)
    // =====================================================

    private bool IsSessionActiveWith(string remoteProtocolPeerId)
        => _stream != null &&
           _currentSessionId != Guid.Empty &&
           _remotePeerId == remoteProtocolPeerId;

    public async Task EnsureSessionAsync(
        string remoteProtocolPeerId,
        CancellationToken ct = default)
    {
        if (IsSessionActiveWith(remoteProtocolPeerId))
            return;

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (IsSessionActiveWith(remoteProtocolPeerId))
                return;

            await _peer.OpenSessionAsync(remoteProtocolPeerId, this, ct);

            if (!IsSessionActiveWith(remoteProtocolPeerId))
                throw new InvalidOperationException("LLM session not active after connect.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    // =====================================================
    // CLIENT SIDE
    // =====================================================

    public async Task SendQueryAsync(
        string remoteProtocolPeerId,
        string prompt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        await EnsureSessionAsync(remoteProtocolPeerId, ct);

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

    // =====================================================
    // IZcspService IMPLEMENTATION
    // =====================================================

    public void BindStream(NetworkStream stream)
    {
        _stream = stream;
    }

    public Task OnSessionStartedAsync(Guid sessionId, string remotePeerId)
    {
        _currentSessionId = sessionId;
        _remotePeerId = remotePeerId;
        return Task.CompletedTask;
    }

    public async Task OnSessionDataAsync(Guid sessionId, BinaryReader reader)
    {
        var action = BinaryCodec.ReadString(reader);

        switch (action)
        {
            case "AiQuery":
                {
                    var prompt = BinaryCodec.ReadString(reader);
                    await HandleAiQueryAsync(sessionId, prompt);
                    break;
                }

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
        _stream = null;
        _currentSessionId = Guid.Empty;
        _remotePeerId = null;
        return Task.CompletedTask;
    }

    // =====================================================
    // HOST SIDE (Local Ollama Execution)
    // =====================================================

    private async Task HandleAiQueryAsync(Guid sessionId, string prompt)
    {
        try
        {
            if (_stream == null)
                return;

            if (prompt.Length > 4000)
                prompt = prompt[..4000];

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
        }
        catch (TaskCanceledException)
        {
            // timeout
        }
        catch (IOException)
        {
            // stream closed
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AI error: {ex.Message}");
        }
    }

    // =====================================================
    // LOCAL OLLAMA CALL
    // =====================================================

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