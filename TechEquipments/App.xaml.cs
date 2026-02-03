using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CtApi;
using Microsoft.EntityFrameworkCore;

namespace TechEquipments
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static IHost AppHost { get; private set; } = null!;

        public App()
        {
            AppHost = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.SetBasePath(AppContext.BaseDirectory);
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                })
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices((context, services) =>
                {
                    // EF Core DbContextFactory
                    string connStr = context.Configuration.GetConnectionString("Postgres");                    
                    services.AddDbContextFactory<PgDbContext>(options => options.UseNpgsql(connStr));
                    services.AddSingleton<IDbService, PgDbService>();

                    services.AddSingleton<CtApiService>();
                    services.AddSingleton<ICtApiService>(sp => sp.GetRequiredService<CtApiService>());
                    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CtApiService>());
                    services.AddSingleton<IEquipmentService, EquipmentService>();

                    services.AddSingleton<IUserStateService, JsonUserStateService>();

                    services.AddTransient<MainWindow>();
                })
                .Build();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            await AppHost.StartAsync();
            AppHost.Services.GetRequiredService<MainWindow>().Show();

            // Быстрый тест подключения
            using var scope = AppHost.Services.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PgDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var ok = await db.Database.CanConnectAsync();
            if (!ok) throw new Exception("Postgres: cannot connect.");
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            await AppHost.StopAsync();
            AppHost.Dispose();
            base.OnExit(e);
        }

    }
}
