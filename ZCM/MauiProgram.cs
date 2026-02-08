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
    public static class ServiceHelper
    {
        public static IServiceProvider Services { get; private set; } = default!;
        public static void Initialize(IServiceProvider serviceProvider) => Services = serviceProvider;
        public static T GetService<T>() => Services.GetService<T>()!;
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

            builder.Services.AddDbContext<ServiceDBContext>(options =>
            {
                var dbPath = Path.Combine(FileSystem.AppDataDirectory, Config.DBFileName);
                options.UseSqlite($"Data Source={dbPath}",
                    b => b.MigrationsAssembly("ZCM"));
            });

            builder.Services.AddSingleton<DataStore>();

            // scoped repos (EF)
            builder.Services.AddScoped<IPeerRepository, PeerRepository>();
            builder.Services.AddScoped<IMessageRepository, MessageRepository>();
            builder.Services.AddScoped<IChatQueryService, ChatQueryService>();

            // ZCSP
            builder.Services.AddSingleton<SessionRegistry>();
            builder.Services.AddSingleton<ZcspPeer>();

            // IMPORTANT: singleton so UI + background share ONE instance
            builder.Services.AddSingleton<MessagingService>();

#if DEBUG
            builder.Logging.AddDebug();
#endif
            var app = builder.Build();

            // Ensure DB exists
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
                db.Database.EnsureCreated();
            }

            ServiceHelper.Initialize(app.Services);

            // Init store from DB (optional / UI)
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
                var store = scope.ServiceProvider.GetRequiredService<DataStore>();

                foreach (var peer in db.PeerNodes.ToList())
                    store.Peers.Add(peer);
            }

            // Start discovery
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
                var store = scope.ServiceProvider.GetRequiredService<DataStore>();

                var multicastAddress = IPAddress.Parse(Config.MulticastAddress);
                string dbPath = db.Database.GetDbConnection().DataSource;

                Task.Run(() =>
                    ZCDPPeer.StartAndRun(multicastAddress, Config.Port, dbPath, store)
                );
            }

            // Force MessagingService creation at startup -> starts hosting even if MessagingPage never opened
            _ = app.Services.GetRequiredService<MessagingService>();

            return app;
        }
    }
}
