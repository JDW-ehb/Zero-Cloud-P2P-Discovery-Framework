using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using System.Net;
using ZCL.API;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Sessions;
using ZCL.Repositories.Messages;
using ZCL.Repositories.Peers;
using ZCL.Services.Messaging;
using ZCL.Services.FileSharing;

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
        var optionsBuilder = new DbContextOptionsBuilder<ServiceDBContext>();
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Config.DBFileName);

        optionsBuilder.UseSqlite($"Data Source={dbPath}");
        return new ServiceDBContext(optionsBuilder.Options);
    }
}

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // =========================
        // Database
        // =========================
        builder.Services.AddDbContext<ServiceDBContext>(options =>
        {
            var dbPath = Path.Combine(FileSystem.AppDataDirectory, Config.DBFileName);
            options.UseSqlite($"Data Source={dbPath}", b => b.MigrationsAssembly("ZCM"));
        });

        builder.Services.AddSingleton<DataStore>();

        // =========================
        // Repositories
        // =========================
        builder.Services.AddScoped<IPeerRepository, PeerRepository>();
        builder.Services.AddScoped<IMessageRepository, MessageRepository>();
        builder.Services.AddScoped<IChatQueryService, ChatQueryService>();

        // =========================
        // ZCSP core
        // =========================
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<ZcspPeer>();

        // =========================
        // Services
        // =========================
        builder.Services.AddSingleton<MessagingService>();

        builder.Services.AddSingleton<Func<string>>(_ =>
        {
            return () =>
            {
                var dir = Path.Combine(FileSystem.AppDataDirectory, "Downloads");
                Directory.CreateDirectory(dir);
                return dir;
            };
        });

        builder.Services.AddSingleton<FileSharingService>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // =========================
        // Ensure DB exists
        // =========================
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
            db.Database.EnsureCreated();
        }

        ServiceHelper.Initialize(app.Services);

        // =========================
        // Init DataStore from DB
        // =========================
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
            var store = scope.ServiceProvider.GetRequiredService<DataStore>();

            foreach (var dbPeer in db.PeerNodes.ToList())
                store.Peers.Add(dbPeer);
        }

        // =========================
        // Start discovery (ZCDP)
        // =========================
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
            var store = scope.ServiceProvider.GetRequiredService<DataStore>();

            var multicastAddress = IPAddress.Parse(Config.MulticastAddress);
            string dbPath = db.Database.GetDbConnection().DataSource;

            Task.Run(() =>
                ZCDPPeer.StartAndRun(
                    multicastAddress,
                    Config.Port,
                    dbPath,
                    store)
            );
        }

        // =========================
        // Start ZCSP hosting
        // =========================
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
                        _ => null
                    };
                })
        );



        // =========================
        // Force service construction
        // =========================
        _ = app.Services.GetRequiredService<MessagingService>();
        _ = app.Services.GetRequiredService<FileSharingService>();

        return app;
    }
}
