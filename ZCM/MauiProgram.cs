using LiveChartsCore.SkiaSharpView.Maui;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using System.Net;
using System.Threading;
using ZCL.API;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Sessions;
using ZCL.Repositories.IA;
using ZCL.Repositories.Messages;
using ZCL.Repositories.Peers;
using ZCL.Repositories.Security;
using ZCL.Security;
using ZCL.Services.FileSharing;
using ZCL.Services.LLM;
using ZCL.Services.Messaging;
using ZCM.Security;
using ZCM.Services;

namespace ZCM;

public static class ServiceHelper
{
    public static IServiceProvider Services { get; private set; } = default!;
    public static void Initialize(IServiceProvider serviceProvider) => Services = serviceProvider;
    public static T GetService<T>() => Services.GetRequiredService<T>();

    public static async Task ResetNetworkBoundaryAsync()
    {
        var peer = GetService<ZcspPeer>();
        var sessions = GetService<SessionRegistry>();
        var trustCache = GetService<TrustGroupCache>();
        var trustRepo = GetService<ITrustGroupRepository>();
        var announceRepo = GetService<IAnnouncedServiceSettingsRepository>();
        var store = GetService<DataStore>();

        Console.WriteLine("[SECURITY] Resetting network boundary...");

        sessions.ClearAll();

        await peer.StopHostingAsync();

        var enabled = await trustRepo.GetEnabledAsync();
        trustCache.SetEnabledSecrets(enabled.Select(x => x.SecretHex));

        peer.StartHosting(
            port: 5555,
            serviceName =>
            {
                return serviceName switch
                {
                    "Messaging" => GetService<MessagingService>(),
                    "FileSharing" => GetService<FileSharingService>(),
                    "LLMChat" => GetService<LLMChatService>(),
                    _ => null
                };
            });

        Console.WriteLine("[SECURITY] Hosting restarted. Reconnecting to known peers...");

        _ = Task.Run(async () =>
        {
            try
            {
                var enabledServices = await announceRepo.GetEnabledNamesAsync();

                foreach (var p in store.Peers.ToList())
                {
                    var ip = p.IpAddress;          
                    var remotePeerId = p.ProtocolPeerId; 

                    if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(remotePeerId))
                        continue;

                    await Task.Delay(50);

                    foreach (var serviceName in enabledServices)
                    {
                        IZcspService? svc = serviceName switch
                        {
                            "Messaging" => GetService<MessagingService>(),
                            "FileSharing" => GetService<FileSharingService>(),
                            "LLMChat" => GetService<LLMChatService>(),
                            _ => null
                        };

                        if (svc == null)
                            continue;

                        try
                        {
                            await peer.ConnectAsync(ip, 5555, remotePeerId, svc);
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[RECONNECT] {serviceName} -> {ip} failed: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RECONNECT] Loop crashed: {ex}");
            }
        });

        Console.WriteLine("[SECURITY] Boundary reset complete.");
    }

}

public class ServiceDBContextFactory : IDesignTimeDbContextFactory<ServiceDBContext>
{
    public ServiceDBContext CreateDbContext(string[] args)
    {
        SqlCipherInitializer.Initialize();

        Config.Instance.AppDataDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Config.Instance.Load();

        var dbPath = Path.Combine(
            Config.Instance.AppDataDirectory,
            Config.Instance.DBFileName);

        var key = Environment.GetEnvironmentVariable("ZC_DB_KEY") ?? "dev-only-key";

        var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared;");

        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA key = '{key}';";
            cmd.ExecuteNonQuery();
        }

        var optionsBuilder = new DbContextOptionsBuilder<ServiceDBContext>();
        optionsBuilder.UseSqlite(connection);

        return new ServiceDBContext(optionsBuilder.Options);
    }
}

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        Config.Instance.AppDataDirectory = FileSystem.AppDataDirectory;
        Config.Instance.Load();

        var builder = MauiApp.CreateBuilder();
        SqlCipherInitializer.Initialize();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });


        var dbKey = "dev-only-key";

        builder.Services.AddDbContext<ServiceDBContext>((sp, options) =>
        {
            var dbPath = Path.Combine(
                Config.Instance.AppDataDirectory,
                Config.Instance.DBFileName);

            var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared;");

            connection.Open();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA key = '{dbKey}';";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "PRAGMA cipher_version;";
                var version = cmd.ExecuteScalar();

                if (version == null)
                    throw new InvalidOperationException("SQLCipher not active.");
            }

            options.UseSqlite(connection, b => b.MigrationsAssembly("ZCM"));
        });

        // =============================
        // ✅ NEW: Trust + Announce System
        // =============================

        builder.Services.AddSingleton<TrustGroupCache>();

        builder.Services.AddScoped<ITrustGroupRepository, TrustGroupRepository>();
        builder.Services.AddScoped<IAnnouncedServiceSettingsRepository, AnnouncedServiceSettingsRepository>();

        // =============================

        builder.Services.AddSingleton<DataStore>();

        builder.Services.AddScoped<IPeerRepository, PeerRepository>();
        builder.Services.AddScoped<IMessageRepository, MessageRepository>();
        builder.Services.AddScoped<IChatQueryService, ChatQueryService>();
        builder.Services.AddScoped<ILLMChatRepository, LLMChatRepository>();

        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<RoutingState>();

        builder.Services.AddSingleton<ZcspPeer>();
        builder.Services.AddSingleton<MessagingService>();
        builder.Services.AddSingleton<FileSharingService>();
        builder.Services.AddSingleton<LLMChatService>();
        builder.Services.AddSingleton<ActivityService>();

        builder.Services.AddSingleton<Func<string>>(_ =>
        {
            return () =>
            {
                var dir = Path.Combine(Config.Instance.AppDataDirectory, "Downloads");
                Directory.CreateDirectory(dir);
                return dir;
            };
        });


#if DEBUG
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();

        // Only log warnings+ from EF Core
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

        // Silence SQL command spam specifically
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

        // Allow your own namespaces at Debug level
        builder.Logging.AddFilter("ZCL", LogLevel.Debug);
        builder.Logging.AddFilter("ZCM", LogLevel.Debug);
#endif

        var app = builder.Build();

        var routingState = app.Services.GetRequiredService<RoutingState>();
        routingState.Initialize(NodeRole.Peer);

        // =============================
        // ✅ Ensure DB + Trust Defaults
        // =============================

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
            db.Database.EnsureCreated();

            var trustRepo = scope.ServiceProvider.GetRequiredService<ITrustGroupRepository>();
            trustRepo.EnsureDefaultsAsync().GetAwaiter().GetResult();

            // hydrate group trust cache
            var cache = app.Services.GetRequiredService<TrustGroupCache>();

            var enabled = trustRepo.GetEnabledAsync().GetAwaiter().GetResult();
            cache.SetEnabledSecrets(enabled.Select(x => x.SecretHex));
        }

        ServiceHelper.Initialize(app.Services);

        // =============================
        // Load stored peers into DataStore
        // =============================

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
            var store = scope.ServiceProvider.GetRequiredService<DataStore>();

            foreach (var dbPeer in db.PeerNodes.ToList())
                store.Peers.Add(dbPeer);
        }

        // =============================
        // Discovery
        // =============================

        using (var scope = app.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<DataStore>();

            var multicastAddress = IPAddress.Parse(Config.Instance.MulticastAddress);
            var port = Config.Instance.DiscoveryPort;

            var cts = new CancellationTokenSource();

            _ = ZCDPPeer.StartAndRunAsync(
                multicastAddress,
                port,
                () =>
                {
                    var innerScope = app.Services.CreateScope();
                    return innerScope.ServiceProvider.GetRequiredService<ServiceDBContext>();
                },
                store,
                routingState,
                NodeRole.Peer,
                cts.Token);
        }

        // =============================
        // ZCSP Hosting
        // =============================

        var zcspPeer = app.Services.GetRequiredService<ZcspPeer>();

        Task.Run(() =>
            zcspPeer.StartHosting(
                port: 5555,
                serviceName =>
                {
                    return serviceName switch
                    {
                        "Messaging" => app.Services.GetRequiredService<MessagingService>(),
                        "FileSharing" => app.Services.GetRequiredService<FileSharingService>(),
                        "LLMChat" => app.Services.GetRequiredService<LLMChatService>(),
                        _ => null
                    };
                })
        );

        // Warm services
        _ = app.Services.GetRequiredService<MessagingService>();
        _ = app.Services.GetRequiredService<FileSharingService>();
        _ = app.Services.GetRequiredService<LLMChatService>();

        return app;
    }
}