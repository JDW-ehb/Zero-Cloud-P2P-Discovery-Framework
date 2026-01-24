using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel.DataAnnotations;
using System.Net;

using ZCL.API;
using ZCL.Models;

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
            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Config.dbFileName);

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
                var dbPath = Path.Combine(FileSystem.AppDataDirectory, Config.dbFileName);
                options.UseSqlite($"Data Source={dbPath}",
                        b => b.MigrationsAssembly("ZCM"));
            });

#if DEBUG
            builder.Logging.AddDebug();
#endif
            var app = builder.Build();

            ServiceHelper.Initialize(app.Services);

            return app;
        }
    }
}
