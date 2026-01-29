using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System.Diagnostics;
using System.Net;
using ZCL.API;
using ZCL.Models;

namespace ZCM_Console
{
    internal class Program
    {
        public class ServiceDBContextFactory : IDesignTimeDbContextFactory<ServiceDBContext>
        {
            // dotnet ef migrations add InitialCreate --no-build
            // dotnet ef database update --no-build

            public ServiceDBContext CreateDbContext(string[] args)
            {
                var optionsBuilder = new DbContextOptionsBuilder<ServiceDBContext>();
                var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Config.DBFileName);

                optionsBuilder.UseSqlite($"Data Source={dbPath}", b => b.MigrationsAssembly("ZCM-Console"));

                return new ServiceDBContext(optionsBuilder.Options);
            }
        }
        static void Main(string[] args)
        { 
            int port = Config.Port;
            var multicastAddress = IPAddress.Parse(Config.MulticastAddress);
            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Config.DBFileName);
            Debug.WriteLine($"dbPath: {dbPath}");

            var store = new DataStore();

            ZCDPPeer.StartAndRun(multicastAddress, port, dbPath, store);
        }
    }
}
