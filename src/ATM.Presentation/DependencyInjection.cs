using ATM.Presentation.Navigation;
using ATM.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ATM.Presentation;

public static class DependencyInjection
{
    public static IServiceCollection AddPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<CustomerServiceViewModel>();
        services.AddTransient<MaintenanceViewModel>();
        return services;
    }
}
