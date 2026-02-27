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
using ZCL.Services.FileSharing;
using ZCL.Services.LLM;
using ZCL.Services.Messaging;
using ZCM.Security;

namespace ZCM;

public static class ServiceHelper
{
    public static IServiceProvider Services { get; private set; } = default!;
    public static void Initialize(IServiceProvider serviceProvider) => Services = serviceProvider;
    public static T GetService<T>() => Services.GetRequiredService<T>();
}

public class ServiceDBContextFactory : IDesignTimeDbContextFactory<ServiceDBContext>
{
    public ServiceDBContext CreateDbContext(string[] args)
    {
        SqlCipherInitializer.Initialize();

        // ✅ Design-time (migrations) runs outside MAUI, so use OS LocalApplicationData.
        Config.Instance.AppDataDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Load config so DBFileName etc. are consistent with runtime behavior
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
        // ✅ One source of truth for ALL app files (db, cert, config json, etc.)
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
            // ✅ Use the same directory you loaded config from
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

        builder.Services.AddSingleton<DataStore>();

        builder.Services.AddScoped<IPeerRepository, PeerRepository>();
        builder.Services.AddScoped<IMessageRepository, MessageRepository>();
        builder.Services.AddScoped<IChatQueryService, ChatQueryService>();
        builder.Services.AddScoped<ILLMChatRepository, LLMChatRepository>();

        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<LLMChatService>();
        builder.Services.AddSingleton<RoutingState>();
        builder.Services.AddSingleton<ZcspPeer>();

        builder.Services.AddSingleton<MessagingService>();

        builder.Services.AddSingleton<Func<string>>(_ =>
        {
            return () =>
            {
                var dir = Path.Combine(Config.Instance.AppDataDirectory, "Downloads");
                Directory.CreateDirectory(dir);
                return dir;
            };
        });

        builder.Services.AddSingleton<FileSharingService>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        var routingState = app.Services.GetRequiredService<RoutingState>();
        routingState.Initialize(NodeRole.Peer);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
            db.Database.EnsureCreated();
        }

        ServiceHelper.Initialize(app.Services);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
            var store = scope.ServiceProvider.GetRequiredService<DataStore>();

            foreach (var dbPeer in db.PeerNodes.ToList())
                store.Peers.Add(dbPeer);
        }

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

        var zcspPeer = app.Services.GetRequiredService<ZcspPeer>();

        Task.Run(() =>
            zcspPeer.StartHostingAsync(
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

        _ = app.Services.GetRequiredService<MessagingService>();
        _ = app.Services.GetRequiredService<FileSharingService>();
        _ = app.Services.GetRequiredService<LLMChatService>();

        return app;
    }
}