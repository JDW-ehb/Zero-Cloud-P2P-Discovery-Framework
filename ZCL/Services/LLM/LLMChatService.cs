using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http.Json;
using System.Net.Sockets;
using ZCL.API;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Protocol;
using ZCL.Protocol.ZCSP.Transport;
using ZCL.Repositories.Peers;

namespace ZCL.Services.LLM;

public sealed class LLMChatService : IZcspService
{
    public string ServiceName => "LLMChat";

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<Guid, Stream> _sessions = new();
    private readonly ConcurrentDictionary<string, Task> _connectInFlight = new();
    private readonly ConcurrentDictionary<string, DateTime> _nextAllowedConnectUtc = new();

    private readonly IServiceScopeFactory _scopeFactory;

    private readonly ZcspPeer _peer;
    private readonly RoutingState _routingState;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private Guid? _serverSessionId;

    private readonly ConcurrentDictionary<string, Guid> _directSessions = new();

    private readonly ConcurrentDictionary<Guid, SessionContext> _contexts = new();

    private readonly ConcurrentDictionary<Guid, Guid> _pendingRequests = new();

    private readonly ConcurrentDictionary<Guid, Guid> _sessionConversations = new();

    private sealed record SessionContext(Stream Stream, string RemoteProtocolPeerId);

    public event Func<string, Task>? ResponseReceived;
    public event Action<Guid, string>? SessionStarted;

    public LLMChatService(
        ZcspPeer peer,
        RoutingState routingState,
        IServiceScopeFactory scopeFactory)
    {
        _peer = peer;
        _routingState = routingState;
        _scopeFactory = scopeFactory;

        _http = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:11434"),
            Timeout = TimeSpan.FromSeconds(60)
        };
    }


    public async Task OnSessionStartedAsync(Guid sessionId, string remotePeerId, Stream stream)
    {
        _sessions[sessionId] = stream;
        _contexts[sessionId] = new SessionContext(stream, remotePeerId);

        if (_routingState.Mode == RoutingMode.ViaServer &&
            !string.IsNullOrWhiteSpace(_routingState.ServerProtocolPeerId) &&
            remotePeerId == _routingState.ServerProtocolPeerId)
        {
            _serverSessionId = sessionId;
        }
        else
        {
            _directSessions[remotePeerId] = sessionId;
        }

        SessionStarted?.Invoke(sessionId, remotePeerId);

        if (await IsOllamaAvailableAsync())
            await TryCreateHostConversationAsync(sessionId, remotePeerId);
    }

    public Task OnSessionClosedAsync(Guid sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        _sessionConversations.TryRemove(sessionId, out _);

        if (_contexts.TryRemove(sessionId, out var ctx))
        {
            _directSessions.TryRemove(ctx.RemoteProtocolPeerId, out _);
        }

        if (_serverSessionId == sessionId)
            _serverSessionId = null;

        return Task.CompletedTask;
    }

    public async Task OnSessionDataAsync(Guid sessionId, BinaryReader reader)
    {
        var action = BinaryCodec.ReadString(reader);

        switch (action)
        {
            case "AiQuery2":
                {
                    var requestId = BinaryCodec.ReadGuid(reader);
                    var prompt = BinaryCodec.ReadString(reader);
                    await HandleAiQueryAsync(sessionId, requestId, prompt);
                    break;
                }

            case "AiQueryFor2":
                {
                    var requestId = BinaryCodec.ReadGuid(reader);
                    var targetProtocolPeerId = BinaryCodec.ReadString(reader);
                    var prompt = BinaryCodec.ReadString(reader);

                    if (_routingState.Role != NodeRole.Server)
                        return;

                    await HandleAiQueryForAsync(sessionId, requestId, targetProtocolPeerId, prompt);
                    break;
                }

            case "AiResponse2":
                {
                    var requestId = BinaryCodec.ReadGuid(reader);
                    var response = BinaryCodec.ReadString(reader);

                    if (_routingState.Role == NodeRole.Server &&
                        _pendingRequests.TryRemove(requestId, out var requesterSessionId) &&
                        _contexts.TryGetValue(requesterSessionId, out var requesterCtx))
                    {
                        var forward = BinaryCodec.Serialize(
                            ZcspMessageType.SessionData,
                            requesterSessionId,
                            w =>
                            {
                                BinaryCodec.WriteString(w, "AiResponse2");
                                BinaryCodec.WriteGuid(w, requestId);
                                BinaryCodec.WriteString(w, response);
                            });

                        await Framing.WriteAsync(requesterCtx.Stream, forward);
                        return;
                    }

                    if (ResponseReceived != null)
                    {
                        foreach (Func<string, Task> handler in ResponseReceived.GetInvocationList())
                            await handler(response);
                    }
                    break;
                }
        }
    }


    private async Task HandleAiQueryForAsync(
        Guid requesterSessionId,
        Guid requestId,
        string targetProtocolPeerId,
        string prompt)
    {
        if (_routingState.Role != NodeRole.Server)
            return;

        using var scope = _scopeFactory.CreateScope();
        var peersRepo = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

        var targetPeer = await peersRepo.GetByProtocolPeerIdAsync(targetProtocolPeerId);
        if (targetPeer == null)
            return;

        await EnsureSessionAsync(targetPeer);

        if (!_directSessions.TryGetValue(targetProtocolPeerId, out var serverToHostSessionId))
            return;

        if (!_contexts.TryGetValue(serverToHostSessionId, out var hostCtx))
            return;

        _pendingRequests[requestId] = requesterSessionId;

        var forward = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            serverToHostSessionId,
            w =>
            {
                BinaryCodec.WriteString(w, "AiQuery2");
                BinaryCodec.WriteGuid(w, requestId);
                BinaryCodec.WriteString(w, prompt);
            });

        await Framing.WriteAsync(hostCtx.Stream, forward);
    }


    private async Task HandleAiQueryAsync(Guid sessionId, Guid requestId, string prompt)
    {
        try
        {
            if (!_sessions.TryGetValue(sessionId, out var stream))
                return;

            if (prompt.Length > 4000)
                prompt = prompt[..4000];

            Guid? convoId = null;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

                if (_sessionConversations.TryGetValue(sessionId, out var cid))
                {
                    convoId = cid;
                    db.LLMMessages.Add(new LLMMessageEntity
                    {
                        Id = Guid.NewGuid(),
                        ConversationId = cid,
                        Content = prompt,
                        IsUser = true,
                        Timestamp = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync();
                }
            }

            var reply = await GenerateLocalAsync(prompt);

            if (convoId.HasValue)
            {
                using var scope2 = _scopeFactory.CreateScope();
                var db2 = scope2.ServiceProvider.GetRequiredService<ServiceDBContext>();

                db2.LLMMessages.Add(new LLMMessageEntity
                {
                    Id = Guid.NewGuid(),
                    ConversationId = convoId.Value,
                    Content = reply,
                    IsUser = false,
                    Timestamp = DateTime.UtcNow
                });

                await db2.SaveChangesAsync();
            }

            var responseMsg = BinaryCodec.Serialize(
                ZcspMessageType.SessionData,
                sessionId,
                w =>
                {
                    BinaryCodec.WriteString(w, "AiResponse2");
                    BinaryCodec.WriteGuid(w, requestId);
                    BinaryCodec.WriteString(w, reply);
                });

            await Framing.WriteAsync(stream, responseMsg);
        }
        catch (TaskCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"AI error: {ex.Message}");
        }
    }

    private async Task<bool> IsOllamaAvailableAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
            var res = await _http.GetAsync("/api/tags", cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task TryCreateHostConversationAsync(Guid sessionId, string remotePeerId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
            var peersRepo = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

            var remotePeer = await peersRepo.GetByProtocolPeerIdAsync(remotePeerId);
            if (remotePeer == null)
                return;

            var convo = new LLMConversationEntity
            {
                Id = Guid.NewGuid(),
                PeerId = remotePeer.PeerId,
                Model = "phi3:latest",
                CreatedAt = DateTime.UtcNow
            };

            db.LLMConversations.Add(convo);
            await db.SaveChangesAsync();

            _sessionConversations[sessionId] = convo.Id;
        }
        catch
        {
        }
    }


    public async Task SendQueryRoutedAsync(
        PeerNode? ownerPeer,
        string targetProtocolPeerId,
        string prompt,
        CancellationToken ct = default)
    {
        var (sid, ctx, viaServer) = await GetRouteAsync(ownerPeer, ct);
        var requestId = Guid.NewGuid();

        var msg = BinaryCodec.Serialize(
            ZcspMessageType.SessionData,
            sid,
            w =>
            {
                if (viaServer)
                {
                    BinaryCodec.WriteString(w, "AiQueryFor2");
                    BinaryCodec.WriteGuid(w, requestId);
                    BinaryCodec.WriteString(w, targetProtocolPeerId);
                    BinaryCodec.WriteString(w, prompt);
                }
                else
                {
                    BinaryCodec.WriteString(w, "AiQuery2");
                    BinaryCodec.WriteGuid(w, requestId);
                    BinaryCodec.WriteString(w, prompt);
                }
            });

        await Framing.WriteAsync(ctx.Stream, msg);
    }

    private async Task<(Guid sessionId, SessionContext ctx, bool viaServer)>
        GetRouteAsync(PeerNode? directPeer, CancellationToken ct)
    {
        if (_routingState.Mode == RoutingMode.ViaServer)
        {
            if (_serverSessionId is Guid sid && _contexts.TryGetValue(sid, out var sctx))
                return (sid, sctx, true);

            if (await EnsureServerSessionAsync(ct) &&
                _serverSessionId is Guid sid2 &&
                _contexts.TryGetValue(sid2, out var sctx2))
            {
                return (sid2, sctx2, true);
            }
        }

        if (directPeer == null)
            throw new InvalidOperationException("No server and no direct peer.");

        if (_directSessions.TryGetValue(directPeer.ProtocolPeerId, out var dsid) &&
            _contexts.TryGetValue(dsid, out var dctx))
            return (dsid, dctx, false);

        await EnsureSessionAsync(directPeer, ct);

        if (_directSessions.TryGetValue(directPeer.ProtocolPeerId, out var dsid2) &&
            _contexts.TryGetValue(dsid2, out var dctx2))
            return (dsid2, dctx2, false);

        throw new InvalidOperationException("Direct session failed.");
    }

    public async Task EnsureSessionAsync(PeerNode peer, CancellationToken ct = default)
    {
        if (_directSessions.ContainsKey(peer.ProtocolPeerId))
            return;

        // Backoff gate
        if (_nextAllowedConnectUtc.TryGetValue(peer.ProtocolPeerId, out var next) &&
            DateTime.UtcNow < next)
            return;

        // Single-flight per peer
        var task = _connectInFlight.GetOrAdd(peer.ProtocolPeerId, _ => ConnectOnceAsync(peer, ct));

        try
        {
            await task;
        }
        finally
        {
            _connectInFlight.TryRemove(new KeyValuePair<string, Task>(peer.ProtocolPeerId, task));
        }
    }

    private async Task ConnectOnceAsync(PeerNode peer, CancellationToken ct)
    {
        if (_directSessions.ContainsKey(peer.ProtocolPeerId))
            return;

        try
        {
            await _peer.ConnectAsync(peer.IpAddress, 5555, peer.ProtocolPeerId, this, ct);
        }
        catch (UnauthorizedAccessException)
        {
            _nextAllowedConnectUtc[peer.ProtocolPeerId] = DateTime.UtcNow.AddSeconds(10);
            throw;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            _nextAllowedConnectUtc[peer.ProtocolPeerId] = DateTime.UtcNow.AddSeconds(2);
            throw;
        }
        catch
        {
            _nextAllowedConnectUtc[peer.ProtocolPeerId] = DateTime.UtcNow.AddSeconds(3);
            throw;
        }
    }

    public async Task<bool> EnsureServerSessionAsync(CancellationToken ct = default)
    {
        if (_routingState.Mode != RoutingMode.ViaServer)
            return false;

        if (_serverSessionId is Guid sid && _contexts.ContainsKey(sid))
            return true;

        if (string.IsNullOrWhiteSpace(_routingState.ServerProtocolPeerId))
            return false;

        using var scope = _scopeFactory.CreateScope();
        var peers = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

        var server = await peers.GetByProtocolPeerIdAsync(_routingState.ServerProtocolPeerId);
        if (server == null)
            return false;

        try
        {
            await _peer.ConnectAsync(server.IpAddress, 5555, server.ProtocolPeerId, this, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }


    public async Task<string> GenerateLocalAsync(string prompt)
    {
        try
        {
            var httpResponse = await _http.PostAsJsonAsync(
                "/api/generate",
                new
                {
                    model = "phi3:latest",
                    prompt,
                    stream = false
                });

            httpResponse.EnsureSuccessStatusCode();

            var result = await httpResponse.Content.ReadFromJsonAsync<OllamaResponse>();
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