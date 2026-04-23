using Microsoft.Extensions.Configuration;
using System;
using System.Reflection;

namespace TechEquipments
{
    /// <summary>
    /// Реализация общего runtime-контекста приложения.
    /// </summary>
    public sealed class AppRuntimeContext : IAppRuntimeContext
    {
        public string DeviceName { get; }
        public bool IsTablet { get; }
        public string AppVersion { get; }

        public AppRuntimeContext(IConfiguration config)
        {
            DeviceName = ResolveDeviceName(config);
            IsTablet = ResolveIsTablet(config);
            AppVersion = ResolveAppVersion();
        }

        private static string ResolveDeviceName(IConfiguration config)
        {
            var configured = (config["Global:DeviceName"] ?? "").Trim();

            var value = string.IsNullOrWhiteSpace(configured)
                ? Environment.MachineName
                : configured;

            value = (value ?? "").Trim();

            return string.IsNullOrWhiteSpace(value)
                ? "UNKNOWN_DEVICE"
                : value.ToUpperInvariant();
        }

        private static bool ResolveIsTablet(IConfiguration config)
        {
            return bool.TryParse(config["Global:IsTablet"], out var value) && value;
        }

        private static string ResolveAppVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();

            var attr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var version = attr?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString()
                          ?? "1.0.0";

            var plusIndex = version.IndexOf('+');
            return plusIndex >= 0 ? version[..plusIndex] : version;
        }
    }
}