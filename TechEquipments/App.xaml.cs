using System;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CtApi;
using TechEquipments.Services.QR;
using Microsoft.EntityFrameworkCore;
using TechEquipments.ViewModels;

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
                    services.AddSingleton<IEquipInfoService, EquipInfoService>();

                    services.AddSingleton<CtApiService>();
                    services.AddSingleton<ICtApiService>(sp => sp.GetRequiredService<CtApiService>());
                    services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CtApiService>());
                    services.AddSingleton<IEquipmentService, EquipmentService>();

                    services.AddSingleton<IUserStateService, JsonUserStateService>();

                    services.AddSingleton<IQrCodeService, QrCodeService>();
                    services.AddSingleton<IQrScannerService, QrScannerService>();

                    services.AddSingleton<MainViewModel>();
                    services.AddTransient<MainWindow>();
                })
                .Build();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            await AppHost.StartAsync();

            using var scope = AppHost.Services.CreateScope();

            var dbService = scope.ServiceProvider.GetRequiredService<IDbService>();
            var equipInfoService = scope.ServiceProvider.GetRequiredService<IEquipInfoService>();

            var ok = await dbService.CanConnectAsync();
            if (!ok)
                throw new Exception("Postgres: cannot connect.");

            // При старте гарантируем наличие таблицы Info
            await equipInfoService.EnsureTableAsync();

            AppHost.Services.GetRequiredService<MainWindow>().Show();
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            await AppHost.StopAsync();
            AppHost.Dispose();
            base.OnExit(e);
        }

    }
}
