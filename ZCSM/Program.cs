using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography;
using ZCL.API;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Sessions;
using ZCL.Repositories.IA;
using ZCL.Repositories.Messages;
using ZCL.Repositories.Peers;
using ZCL.Services.FileSharing;
using ZCL.Services.LLM;
using ZCL.Services.Messaging;

namespace ZCSM;

internal static class Program
{
    private const int ZcspPort = 5555;
    private const string DbKeyEnvVar = "ZC_DB_KEY";

    public static async Task Main(string[] args)
    {
        // ============================================
        // 1️⃣ Initialize SQLCipher provider EARLY
        // ============================================
        SqlCipherInitializer.Initialize();

        var host = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices(services =>
            {
                // ============================================
                // Config
                // ============================================
                Config.Instance.PeerName =
                    $"{Environment.MachineName} (ZCSM)";

                var appDir =
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                var dbPath = Path.Combine(
                    appDir,
                    Config.Instance.DBFileName);

                var dbKeyHex = GetOrCreateDatabaseKey(appDir);

                // ============================================
                // Database (SQLCipher enabled)
                // ============================================
                services.AddDbContext<ServiceDBContext>(options =>
                {
                    var connection = new SqliteConnection(
                        $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared;");

                    connection.Open();

                    using (var cmd = connection.CreateCommand())
                    {
                        // IMPORTANT: key is hex
                        cmd.CommandText = $"PRAGMA key = \"x'{dbKeyHex}'\";";
                        cmd.ExecuteNonQuery();

                        // sanity check
                        cmd.CommandText = "PRAGMA cipher_version;";
                        var version = cmd.ExecuteScalar();

                        if (version == null)
                            throw new InvalidOperationException(
                                "SQLCipher not active. cipher_version is null.");
                    }

                    options.UseSqlite(connection);
                });

                services.AddSingleton<DataStore>();

                // ============================================
                // Repositories
                // ============================================
                services.AddScoped<IPeerRepository, PeerRepository>();
                services.AddScoped<IMessageRepository, MessageRepository>();
                services.AddScoped<IChatQueryService, ChatQueryService>();
                services.AddScoped<ILLMChatRepository, LLMChatRepository>();

                // ============================================
                // ZCSP core
                // ============================================
                services.AddSingleton<SessionRegistry>();
                services.AddSingleton<ZcspPeer>();
                services.AddSingleton<LLMChatService>();
                services.AddSingleton<RoutingState>();

                // ============================================
                // Hosted services
                // ============================================
                services.AddSingleton<MessagingService>();

                services.AddSingleton<Func<string>>(_ =>
                {
                    return () =>
                    {
                        var dir = Path.Combine(appDir, "ZCSM_Downloads");
                        Directory.CreateDirectory(dir);
                        return dir;
                    };
                });

                services.AddSingleton<FileSharingService>();
            })
            .Build();

        // ============================================
        // Routing state
        // ============================================
        var routingState =
            host.Services.GetRequiredService<RoutingState>();

        routingState.Initialize(NodeRole.Server);

        // ============================================
        // Ensure DB exists
        // ============================================
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                          .GetRequiredService<ServiceDBContext>();

            db.Database.EnsureCreated();
        }

        // ============================================
        // Graceful shutdown support
        // ============================================
        var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var multicastAddress =
            IPAddress.Parse(Config.Instance.MulticastAddress);

        var discoveryPort = Config.Instance.DiscoveryPort;

        var store =
            host.Services.GetRequiredService<DataStore>();

        // ============================================
        // ZCDP Discovery (SERVER role)
        // ============================================
        _ = Task.Run(() =>
            ZCDPPeer.StartAndRunAsync(
                multicastAddress,
                discoveryPort,
                () =>
                {
                    var scope = host.Services.CreateScope();
                    return scope.ServiceProvider
                                .GetRequiredService<ServiceDBContext>();
                },
                store,
                routingState,
                localRole: NodeRole.Server,
                ct: cts.Token));

        // ============================================
        // ZCSP Hosting (TLS handled inside ZcspPeer)
        // ============================================
        var zcspPeer =
            host.Services.GetRequiredService<ZcspPeer>();

        _ = Task.Run(() =>
            zcspPeer.StartHostingAsync(
                port: ZcspPort,
                serviceName =>
                {
                    return serviceName switch
                    {
                        "Messaging" =>
                            host.Services.GetRequiredService<MessagingService>(),

                        "FileSharing" =>
                            host.Services.GetRequiredService<FileSharingService>(),

                        "LLMChat" =>
                            host.Services.GetRequiredService<LLMChatService>(),

                        _ => null
                    };
                }));

        await host.RunAsync(cts.Token);
    }

    // ============================================
    // SQLCipher Key Management (Server side)
    // ============================================
    private static string GetOrCreateDatabaseKey(string baseDir)
    {
        // 1️⃣ environment variable wins
        var envKey = Environment.GetEnvironmentVariable(DbKeyEnvVar);
        if (!string.IsNullOrWhiteSpace(envKey))
            return envKey.Trim();

        // 2️⃣ fallback: persistent key file
        var keyFile = Path.Combine(baseDir, "zc_sqlcipher_key_v1.txt");

        if (File.Exists(keyFile))
            return File.ReadAllText(keyFile).Trim();

        var bytes = RandomNumberGenerator.GetBytes(32);
        var hex = Convert.ToHexString(bytes);

        File.WriteAllText(keyFile, hex);
        return hex;
    }
}
internal static class SqlCipherInitializer
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;

        SQLitePCL.Batteries_V2.Init();
        SQLitePCL.raw.SetProvider(
            new SQLitePCL.SQLite3Provider_e_sqlcipher());

        _initialized = true;
    }
}