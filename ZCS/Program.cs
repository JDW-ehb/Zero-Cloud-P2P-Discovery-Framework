using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using ZCL.API;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Sessions;
using ZCL.Repositories.Messages;
using ZCL.Repositories.Peers;
using ZCL.Services.Messaging;

namespace ZCS
{
    internal class Program
    {
        private const int ZcspPort = 5555;

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Zero-Cloud Coordinator ===");

            // ==========================================
            // Enable coordinator mode
            // ==========================================
            Config.Instance.IsCoordinator = true;

            Console.WriteLine($"Machine: {Environment.MachineName}");
            Console.WriteLine("Coordinator Mode: ENABLED");
            Console.WriteLine();

            // ==========================================
            // Setup Dependency Injection
            // ==========================================
            var services = new ServiceCollection();

            services.AddSingleton<Config>();

            services.AddDbContext<ServiceDBContext>(options =>
            {
                var dbPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Config.Instance.DBFileName);

                options.UseSqlite($"Data Source={dbPath}");
            });

            services.AddSingleton<DataStore>();
            services.AddScoped<IPeerRepository, PeerRepository>();
            services.AddScoped<IMessageRepository, MessageRepository>();

            services.AddSingleton<SessionRegistry>();
            services.AddSingleton<ZcspPeer>();

            // Messaging service is scoped (per connection/session)
            services.AddScoped<MessagingService>();

            var provider = services.BuildServiceProvider();

            // ==========================================
            // Ensure DB exists
            // ==========================================
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
                db.Database.EnsureCreated();
            }

            // ==========================================
            // Bootstrap local peer once
            // ==========================================
            Guid localPeerGuid;

            using (var scope = provider.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IPeerRepository>();

                var protocolId = await repo.GetOrCreateLocalProtocolPeerIdAsync(
                    Config.Instance.PeerName,
                    "127.0.0.1");

                localPeerGuid = Guid.Parse(protocolId);
            }

            // ==========================================
            // Load peers into DataStore
            // ==========================================
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
                var store = scope.ServiceProvider.GetRequiredService<DataStore>();

                foreach (var dbPeer in db.PeerNodes.ToList())
                    store.Peers.Add(dbPeer);
            }

            // ==========================================
            // Start Discovery (ZCDP)
            // ==========================================
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();
                var store = scope.ServiceProvider.GetRequiredService<DataStore>();

                var multicastAddress = IPAddress.Parse(Config.Instance.MulticastAddress);
                string dbPath = db.Database.GetDbConnection().DataSource;

                _ = Task.Run(() =>
                    ZCDPPeer.StartAndRunAsync(
                        multicastAddress,
                        Config.Instance.DiscoveryPort,
                        dbPath,
                        store,
                        localPeerGuid,
                        CancellationToken.None)
                );
            }

            Console.WriteLine("Discovery started.");

            // ==========================================
            // Start ZCSP Host (Hub Mode)
            // ==========================================
            var zcspPeer = provider.GetRequiredService<ZcspPeer>();

            _ = Task.Run(() =>
                zcspPeer.StartHostingAsync(
                    port: ZcspPort,
                    serviceResolver: serviceName =>
                    {
                        if (serviceName == "Messaging")
                        {
                            // IMPORTANT:
                            // New scope per service instance
                            var scope = provider.CreateScope();
                            return scope.ServiceProvider
                                        .GetRequiredService<MessagingService>();
                        }

                        return null;
                    })
            );

            Console.WriteLine($"Routing host started on TCP {ZcspPort}");
            Console.WriteLine();
            Console.WriteLine("Press Ctrl+C to exit.");

            await Task.Delay(Timeout.Infinite);
        }
    }
}