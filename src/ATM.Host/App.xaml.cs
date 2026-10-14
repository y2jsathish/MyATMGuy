using System.Windows;
using ATM.Application;
using ATM.Application.Configuration;
using ATM.Application.Jobs;
using ATM.Infrastructure;
using ATM.Infrastructure.Bootstrap;
using ATM.Infrastructure.Logging;
using ATM.Infrastructure.Persistence;
using ATM.Presentation;
using ATM.Presentation.Navigation;
using ATM.Presentation.ViewModels;
using ATM.UI;
using ATM.UI.Shell;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ATM.Host;

// The base class (System.Windows.Application) is declared in the generated half of this partial class.
// It is not named here because the ATM.Application namespace would shadow it.
public partial class App
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Fully qualified: inside namespace ATM.Host the simple name "Host" would resolve to the namespace.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = e.Args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        }));

        var basePath = AppContext.BaseDirectory;
        var start = PlatformBootstrap.Prepare(
            basePath,
            AtmEnvironments.FromEnvironment(),
            Environment.GetEnvironmentVariable("ATM_KEYS_DIR"));
        if (!start.Configuration.IsValid && !start.Configuration.UsedLastKnownGood)
        {
            MessageBox.Show(
                "The terminal cannot start because its configuration is invalid:\n\n" + string.Join("\n", start.Configuration.Errors),
                "ATM configuration error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(start.Decrypted);

        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
        builder.Services.Configure<UiOptions>(builder.Configuration.GetSection(UiOptions.SectionName));
        builder.Services
            .AddPlatform(start, builder.Configuration)
            .AddPresentation()
            .AddUi();

        _host = builder.Build();
        // The database must be migrated before any hosted service (jobs, config reload) can touch it.
        await _host.Services.GetRequiredService<DatabaseMaintenance>().InitializeAsync();
        await _host.StartAsync();

        var hooks = _host.Services.GetRequiredService<UnhandledExceptionHooks>();
        hooks.Install();
        DispatcherUnhandledException += (_, args) =>
        {
            hooks.Handle(args.Exception, "Dispatcher");
            args.Handled = true;
        };

        var watchdog = _host.Services.GetRequiredService<Watchdog>();
        var heartbeat = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (_, _) => watchdog.Beat(), Dispatcher);
        heartbeat.Start();

        _host.Services.GetRequiredService<INavigationService>().NavigateTo<CustomerServiceViewModel>();
        _host.Services.GetRequiredService<ShellWindow>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            // Flush logs and the database within 10 seconds, then dispose (which closes Serilog and the event bus).
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _host.StopAsync(grace.Token);
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
