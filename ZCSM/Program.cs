using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using ZCL.API;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Sessions;
using ZCL.Repositories.IA;
using ZCL.Repositories.Messages;
using ZCL.Repositories.Peers;
using ZCL.Services.FileSharing;
using ZCL.Services.LLM;
using ZCL.Services.Messaging;
using ZCL.Models;

namespace ZCSM;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices(services =>
            {
                // --- Config ---
                // Make server name obvious in discovery
                Config.Instance.PeerName = $"{Environment.MachineName} (ZCSM)";

                // --- Database ---
                var dbPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Config.Instance.DBFileName);

                services.AddDbContext<ServiceDBContext>(opt =>
                    opt.UseSqlite($"Data Source={dbPath}"));

                services.AddSingleton<DataStore>();

                // --- Repos ---
                services.AddScoped<IPeerRepository, PeerRepository>();
                services.AddScoped<IMessageRepository, MessageRepository>();
                services.AddScoped<IChatQueryService, ChatQueryService>();
                services.AddScoped<ILLMChatRepository, LLMChatRepository>();

                // --- ZCSP core ---
                services.AddSingleton<SessionRegistry>();
                services.AddSingleton<ZcspPeer>();
                services.AddSingleton<LLMChatService>();
                services.AddSingleton<RoutingState>(); // not super needed on server, but harmless

                // --- Services hosted by server ---
                services.AddSingleton<MessagingService>();

                services.AddSingleton<Func<string>>(_ =>
                {
                    return () =>
                    {
                        var dir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "ZCSM_Downloads");
                        Directory.CreateDirectory(dir);
                        return dir;
                    };
                });

                services.AddSingleton<FileSharingService>();
            })
            .Build();

        var routingState = host.Services.GetRequiredService<RoutingState>();
        routingState.Initialize(NodeRole.Server);

        // Ensure DB exists
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
            db.Database.EnsureCreated();
        }

        // Start ZCDP + ZCSP
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var multicastAddress = IPAddress.Parse(Config.Instance.MulticastAddress);
        var discoveryPort = Config.Instance.DiscoveryPort;
        const int zcspPort = 5555;

        // Get DB path from DbContext (same as you did in MAUI)
        string dbPathActual;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
            dbPathActual = db.Database.GetDbConnection().DataSource;
        }

        var store = host.Services.GetRequiredService<DataStore>();

        // ZCDP announce as SERVER
        _ = Task.Run(() =>
            ZCDPPeer.StartAndRunAsync(
                multicastAddress,
                discoveryPort,
                dbPathActual,
                store,
                routingState,
                localRole: NodeRole.Server,
                ct: cts.Token));

        // ZCSP hosting (server listens; no outbound dial)
        var zcspPeer = host.Services.GetRequiredService<ZcspPeer>();

        _ = Task.Run(() =>
            zcspPeer.StartHostingAsync(
                port: zcspPort,
                serviceName =>
                {
                    return serviceName switch
                    {
                        "Messaging" => host.Services.GetRequiredService<MessagingService>(),
                        "FileSharing" => host.Services.GetRequiredService<FileSharingService>(),
                        "LLMChat" => host.Services.GetRequiredService<LLMChatService>(),
                        _ => null
                    };
                }));

        // Keep process alive
        await host.RunAsync(cts.Token);
    }
}