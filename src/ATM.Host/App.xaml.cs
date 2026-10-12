using System.Windows;
using ATM.Application;
using ATM.Application.Configuration;
using ATM.Infrastructure;
using ATM.Infrastructure.Configuration;
using ATM.Infrastructure.Logging;
using ATM.Infrastructure.Persistence;
using ATM.Presentation;
using ATM.Presentation.Navigation;
using ATM.Presentation.ViewModels;
using ATM.UI;
using ATM.UI.Shell;
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
        var environment = AtmEnvironments.FromEnvironment();
        var keys = KeyProviderFactory.Create(
            Environment.GetEnvironmentVariable("ATM_KEYS_DIR") ?? System.IO.Path.Combine(basePath, "keys"),
            environment);
        var raw = AtmConfiguration.BuildRaw(basePath, environment);
        var bootstrap = ConfigBootstrap.Load(basePath, environment, new SecretProtector(keys));
        if (!bootstrap.IsValid && !bootstrap.UsedLastKnownGood)
        {
            MessageBox.Show(
                "The terminal cannot start because its configuration is invalid:\n\n" + string.Join("\n", bootstrap.Errors),
                "ATM configuration error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(bootstrap.Configuration.WithDecryption(new SecretProtector(keys)));

        builder.Services.Configure<UiOptions>(builder.Configuration.GetSection(UiOptions.SectionName));
        builder.Services
            .AddAtmConfiguration(raw, bootstrap, environment, keys, basePath)
            .AddApplication()
            .AddInfrastructure()
            .AddAtmPersistence(o => builder.Configuration.GetSection(DatabaseOptions.SectionName).Bind(o))
            .AddPresentation()
            .AddUi();

        _host = builder.Build();
        await _host.StartAsync();

        await _host.Services.GetRequiredService<DatabaseMaintenance>().InitializeAsync();

        var hooks = _host.Services.GetRequiredService<UnhandledExceptionHooks>();
        hooks.Install();
        DispatcherUnhandledException += (_, args) =>
        {
            hooks.Handle(args.Exception, "Dispatcher");
            args.Handled = true;
        };

        _host.Services.GetRequiredService<INavigationService>().NavigateTo<HomeViewModel>();
        _host.Services.GetRequiredService<ShellWindow>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
