using ATM.UI.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace ATM.UI;

public static class DependencyInjection
{
    public static IServiceCollection AddUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ShellWindow>();
        return services;
    }
}
