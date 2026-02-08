using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging;
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
        public static IServiceProvider Services { get; private set; } = default!;
        public static void Initialize(IServiceProvider serviceProvider) => Services = serviceProvider;
        public static T GetService<T>() => Services.GetService<T>()!;
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
        // IMPORTANT: keep this scope alive for the whole app lifetime,
        // otherwise MessagingService (Scoped) gets disposed and hosting stops.
        private static IServiceScope? _messagingScope;

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

            // ZcspPeer is singleton and resolves repo via IServiceScopeFactory internally
            builder.Services.AddSingleton<ZcspPeer>();

            // MessagingService is scoped (uses DbContext-scoped repos), but we will keep one scope alive
            // so it hosts even when MessagingPage is never opened.
            builder.Services.AddScoped<MessagingService>();

#if DEBUG
            builder.Logging.AddDebug();
#endif
            var app = builder.Build();

            // Ensure DB exists
            using (var scope = app.Services.CreateScope())
            {
                var scopedDb = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
                scopedDb.Database.EnsureCreated();
            }

            ServiceHelper.Initialize(app.Services);

            // Load peers into in-memory store (optional / UI)
            {
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
                var store = scope.ServiceProvider.GetRequiredService<DataStore>();

                var peersFromDb = db.PeerNodes.ToList();
                foreach (PeerNode peer in peersFromDb)
                    store.Peers.Add(peer);
            }

            // Run discovery service (kept as your existing pattern)
            {
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
                var store = scope.ServiceProvider.GetRequiredService<DataStore>();

                int port = Config.Port;
                var multicastAddress = IPAddress.Parse(Config.MulticastAddress);
                string dbPath = db.Database.GetDbConnection().DataSource;

                Task.Run(() =>
                {
                    ZCDPPeer.StartAndRun(multicastAddress, port, dbPath, store);
                });
            }

            // Keep the scope alive so the service + its scoped dependencies aren't disposed.
            _messagingScope = app.Services.CreateScope();
            _ = _messagingScope.ServiceProvider.GetRequiredService<MessagingService>();

            return app;
        }
    }
}
