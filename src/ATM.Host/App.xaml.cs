using System.Windows;
using ATM.Application;
using ATM.Infrastructure;
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

        builder.Services.Configure<UiOptions>(builder.Configuration.GetSection(UiOptions.SectionName));
        builder.Services
            .AddApplication()
            .AddInfrastructure()
            .AddPresentation()
            .AddUi();

        _host = builder.Build();
        await _host.StartAsync();

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
