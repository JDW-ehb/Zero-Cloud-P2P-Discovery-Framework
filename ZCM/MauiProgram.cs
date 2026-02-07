using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using ZCL.API;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Sessions;
using ZCL.Repositories.Messages;
using ZCL.Repositories.Peers;
using ZCL.Services.Messaging;

namespace ZCM
{
    // NOTE(luca): Helper to access services from the builder 
    public static class ServiceHelper
    {
        public static IServiceProvider Services { get; private set; }

        public static void Initialize(IServiceProvider serviceProvider) => Services = serviceProvider;

        public static T GetService<T>() => Services.GetService<T>();
    }

    public class ServiceDBContextFactory : IDesignTimeDbContextFactory<ServiceDBContext>
    {
        // dotnet ef migrations add InitialCreate --framework net10.0-windows10.0.19041.0 --no-build
        // dotnet ef database update --framework net10.0-windows10.0.19041.0 --no-build

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

            builder.Services.AddDbContext<ServiceDBContext>(options =>
            {
                var dbPath = Path.Combine(FileSystem.AppDataDirectory, Config.DBFileName);
                options.UseSqlite($"Data Source={dbPath}",
                    b => b.MigrationsAssembly("ZCM"));
            });

            builder.Services.AddSingleton<DataStore>();

            // Repositories / query services
            builder.Services.AddScoped<IPeerRepository, PeerRepository>();
            builder.Services.AddScoped<IMessageRepository, MessageRepository>();
            builder.Services.AddScoped<IChatQueryService, ChatQueryService>();

            // ZCSP peer + sessions
            builder.Services.AddSingleton<SessionRegistry>();

            builder.Services.AddSingleton(sp =>
            {
                var sessions = sp.GetRequiredService<SessionRegistry>();

                // IMPORTANT:
                // If you want discovery + chat to share identity, prefer a GUID string here.
                // For now this keeps your original behavior.
                return new ZcspPeer(Config.peerName, sessions);
            });

            builder.Services.AddScoped<MessagingService>();

#if DEBUG
            builder.Logging.AddDebug();
#endif
            var app = builder.Build();

            // Seed / ensure DB structure once at startup
            using (var scope = app.Services.CreateScope())
            {
                var scopedDb = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
                var peersRepo = scope.ServiceProvider.GetRequiredService<IPeerRepository>();
                var zcspPeer = scope.ServiceProvider.GetRequiredService<ZcspPeer>();

                scopedDb.Database.EnsureCreated();

                peersRepo.EnsureLocalPeerAsync(zcspPeer.PeerId).GetAwaiter().GetResult();

                ServiceDbSeeder
                    .SeedAsync(peersRepo, zcspPeer.PeerId)
                    .GetAwaiter()
                    .GetResult();
            }

            ServiceHelper.Initialize(app.Services);

            var db = ServiceHelper.GetService<ServiceDBContext>();
            db.Database.EnsureCreated();

            var store = ServiceHelper.GetService<DataStore>();

            // Initialize store
            {
                var peersFromDb = db.PeerNodes.ToList();
                foreach (PeerNode peer in peersFromDb)
                    store.Peers.Add(peer);
            }

            // Run discovery service
            {
                int port = Config.Port;
                var multicastAddress = IPAddress.Parse(Config.MulticastAddress);
                string dbPath = db.Database.GetDbConnection().DataSource;

                Task.Run(() =>
                {
                    ZCDPPeer.StartAndRun(multicastAddress, port, dbPath, store);
                });
            }

            return app;
        }
    }
}
