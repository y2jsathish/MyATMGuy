using ATM.Application.Audit;
using ATM.Application.Errors;
using ATM.Application.Terminal;
using ATM.Application.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ATM.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDeviceSelfTestService, DeviceSelfTestService>();
        services.TryAddSingleton(_ => TerminalInfo.FromEnvironment());
        services.TryAddSingleton<IErrorCounters, ErrorCounters>();
        services.TryAddSingleton<IAuditService, AuditService>();
        services.TryAddSingleton<ATM.Application.Transactions.TransactionOptions>();
        return services;
    }
}
