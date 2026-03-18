using System.Runtime.InteropServices;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using SpotDesk.Core.Auth;
using SpotDesk.Core.Crypto;
using SpotDesk.Core.Sync;
using SpotDesk.Core.Vault;
using SpotDesk.Protocols;
using SpotDesk.Protocols.FreeRdp;
using SpotDesk.Protocols.Windows;
using SpotDesk.UI;
using SpotDesk.UI.ViewModels;

namespace SpotDesk.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var services = ConfigureServices();
        AppServices.Configure(services);
        BuildAvaloniaApp(services).StartWithClassicDesktopLifetime(args);
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Crypto & identity
        services.AddSingleton<IDeviceIdService, DeviceIdService>();
        services.AddSingleton<IKeyDerivationService, KeyDerivationService>();
        services.AddSingleton<IKeychainService>(_ => KeychainServiceFactory.Create());

        // Auth — env var overrides take precedence over the bundled client ID
        // (useful for developers testing against their own OAuth App)
        services.AddSingleton<OAuthClientConfig>(_ => OAuthClientConfig.Resolve(null, null, null));
        services.AddSingleton<IOAuthService>(sp => new OAuthService(
            sp.GetRequiredService<IKeychainService>(),
            sp.GetRequiredService<OAuthClientConfig>()));

        // Vault & session lock
        services.AddSingleton<ISessionLockService, SessionLockService>();
        services.AddSingleton<IVaultService, VaultService>();

        // Sync
        services.AddSingleton<IGitSyncService, GitSyncService>();

        // RDP backend — platform-specific
        services.AddSingleton<IRdpBackend>(_ =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new WindowsRdpBackend()
                : new FreeRdpBackend());

        // Session manager
        services.AddSingleton<ISessionManager, SessionManager>();

        // UI services
        services.AddSingleton<ThemeService>();
        services.AddSingleton<LocalPrefsService>();

        // ViewModels
        services.AddTransient<ConnectionTreeViewModel>();
        services.AddTransient<SearchViewModel>(_ => new SearchViewModel([]));
        services.AddTransient<MainWindowViewModel>(sp => new MainWindowViewModel(
            sp.GetRequiredService<ConnectionTreeViewModel>(),
            sp.GetRequiredService<SearchViewModel>(),
            sp.GetRequiredService<IGitSyncService>(),
            sp.GetRequiredService<LocalPrefsService>(),
            sp.GetRequiredService<ThemeService>()));
        services.AddTransient<SettingsViewModel>(sp => new SettingsViewModel(
            sp.GetRequiredService<IOAuthService>(),
            sp.GetRequiredService<IVaultService>(),
            sp.GetRequiredService<ISessionLockService>(),
            sp.GetRequiredService<ThemeService>(),
            sp.GetRequiredService<LocalPrefsService>()));

        return services.BuildServiceProvider();
    }

    private static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder.Configure<SpotDesk.UI.App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                // Use overlay popups to avoid Win32 NativeControlHost child window
                // creation issues when the apphost lacks a supportedOS manifest.
                OverlayPopups = true
            })
            .LogToTrace();
}
