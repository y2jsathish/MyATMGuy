using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ATM.Application.Modules;

/// <summary>
/// One feature's service registrations. Conventions:
/// stateless services and caches are singletons; anything holding per-operation state is scoped (and never injected into a singleton);
/// options objects are singletons. The container is built with ValidateScopes and ValidateOnBuild, so a captive dependency fails at startup.
/// </summary>
public interface IServiceModule
{
    string Name { get; }

    void Register(IServiceCollection services, IConfiguration configuration);
}

public static class ServiceModules
{
    public static IServiceCollection AddModules(this IServiceCollection services, IConfiguration configuration, params IServiceModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            if (!names.Add(module.Name))
            {
                throw new InvalidOperationException($"Module '{module.Name}' is registered twice.");
            }

            module.Register(services, configuration);
        }

        return services;
    }
}
