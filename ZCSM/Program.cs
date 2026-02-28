using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SQLitePCL;
using System.Net;
using System.Security.Cryptography;
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

namespace ZCSM;

internal static class Program
{
    private const int ZcspPort = 5555;
    private const string DbKeyEnvVar = "ZC_DB_KEY";

    public static async Task Main(string[] args)
    {
        SqlCipherInitializer.Initialize();

        var host = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((context, services) =>
            {
                Config.Instance.PeerName =
                    $"{Environment.MachineName} (ZCSM)";

                var appDir =
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                Config.Instance.AppDataDirectory = appDir;
                Config.Instance.Load();

                var dbPath = Path.Combine(
                    appDir,
                    Config.Instance.DBFileName);

                var dbKeyHex = GetOrCreateDatabaseKey(appDir);

                services.AddDbContext<ServiceDBContext>(options =>
                {
                    var connection = new SqliteConnection(
                        $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared;");

                    connection.Open();

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"PRAGMA key = \"x'{dbKeyHex}'\";";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = "PRAGMA cipher_version;";
                    if (cmd.ExecuteScalar() == null)
                        throw new InvalidOperationException("SQLCipher not active.");

                    options.UseSqlite(connection);
                });

                // Core runtime state
                services.AddSingleton<DataStore>();
                services.AddSingleton<SessionRegistry>();
                services.AddSingleton<RoutingState>();
                services.AddSingleton<TrustGroupCache>();

                // Repositories
                services.AddScoped<IPeerRepository, PeerRepository>();
                services.AddScoped<IMessageRepository, MessageRepository>();
                services.AddScoped<IChatQueryService, ChatQueryService>();
                services.AddScoped<ILLMChatRepository, LLMChatRepository>();
                services.AddScoped<ITrustGroupRepository, TrustGroupRepository>();
                services.AddScoped<IAnnouncedServiceSettingsRepository, AnnouncedServiceSettingsRepository>();

                // Services
                services.AddSingleton<ZcspPeer>();
                services.AddSingleton<MessagingService>();
                services.AddSingleton<FileSharingService>();
                services.AddSingleton<LLMChatService>();

                services.AddSingleton<Func<string>>(_ =>
                {
                    return () =>
                    {
                        var dir = Path.Combine(appDir, "ZCSM_Downloads");
                        Directory.CreateDirectory(dir);
                        return dir;
                    };
                });
            })
            .Build();

        var routingState =
            host.Services.GetRequiredService<RoutingState>();

        routingState.Initialize(NodeRole.Server);

        // Ensure DB + trust defaults
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                          .GetRequiredService<ServiceDBContext>();

            db.Database.EnsureCreated();

            var trustRepo =
                scope.ServiceProvider.GetRequiredService<ITrustGroupRepository>();

            await trustRepo.EnsureDefaultsAsync();

            var cache =
                host.Services.GetRequiredService<TrustGroupCache>();

            var enabled =
                await trustRepo.GetEnabledAsync();

            cache.SetEnabledSecrets(enabled.Select(x => x.SecretHex));
        }

        var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var multicastAddress =
            IPAddress.Parse(Config.Instance.MulticastAddress);

        var discoveryPort =
            Config.Instance.DiscoveryPort;

        var store =
            host.Services.GetRequiredService<DataStore>();

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
                NodeRole.Server,
                cts.Token));

        var zcspPeer =
            host.Services.GetRequiredService<ZcspPeer>();

        zcspPeer.StartHosting(
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
            });

        await host.RunAsync(cts.Token);
    }

    private static string GetOrCreateDatabaseKey(string baseDir)
    {
        var envKey = Environment.GetEnvironmentVariable(DbKeyEnvVar);
        if (!string.IsNullOrWhiteSpace(envKey))
            return envKey.Trim();

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

        Batteries_V2.Init();
        raw.SetProvider(new SQLite3Provider_e_sqlcipher());

        _initialized = true;
    }
}