using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using ZCL.API;
using ZCL.Models;
using ZCL.Protocol.ZCSP;
using ZCL.Protocol.ZCSP.Sessions;
using ZCL.Repositories.Peers;

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

            // Config
            services.AddSingleton<Config>();

            // Database
            services.AddDbContext<ServiceDBContext>(options =>
            {
                var dbPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Config.Instance.DBFileName);

                options.UseSqlite($"Data Source={dbPath}");
            });

            services.AddSingleton<DataStore>();

            // Repositories
            services.AddScoped<IPeerRepository, PeerRepository>();

            // ZCSP Core
            services.AddSingleton<SessionRegistry>();
            services.AddSingleton<ZcspPeer>();

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
                    ZCDPPeer.StartAndRun(
                        multicastAddress,
                        Config.Instance.DiscoveryPort,
                        dbPath,
                        store)
                );
            }

            Console.WriteLine("Discovery started.");

            // ==========================================
            // Start ZCSP Routing Host
            // ==========================================
            var zcspPeer = provider.GetRequiredService<ZcspPeer>();

            _ = Task.Run(() =>
                zcspPeer.StartRoutingHostAsync(
                    port: ZcspPort,
                    serviceResolver: serviceName =>
                    {
                        // Coordinator is PURE router.
                        // It does NOT host Messaging/LLM/FileSharing.
                        return null;
                    })
            );

            Console.WriteLine($"Routing host started on TCP {ZcspPort}");
            Console.WriteLine();
            Console.WriteLine("Press Ctrl+C to exit.");

            // ==========================================
            // Keep process alive
            // ==========================================
            await Task.Delay(Timeout.Infinite);
        }
    }
}