using ATM.Application.Errors;
using ATM.Core.Errors;
using Microsoft.Extensions.Logging;

namespace ATM.Infrastructure.Logging;

/// <summary>Makes sure no failure is silent: logs and counts exceptions from the AppDomain, unobserved tasks and (via <see cref="Handle"/>) the UI dispatcher.</summary>
public sealed class UnhandledExceptionHooks(ILogger<UnhandledExceptionHooks> logger, IErrorCounters counters) : IDisposable
{
    private bool installed;

    public void Install()
    {
        if (installed)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnDomain;
        TaskScheduler.UnobservedTaskException += OnTask;
        installed = true;
    }

    /// <summary>Logs and counts an exception. Returns the catalogue code that was recorded.</summary>
    public ErrorCode Handle(Exception exception, string source)
    {
        var code = exception is AtmException atm ? atm.Code : ErrorCatalogue.Unknown;
        counters.Record(code);
        logger.LogError(exception, "Unhandled exception from {Source} ({ErrorCode})", source, code.Code);
        return code;
    }

    public void Dispose()
    {
        if (!installed)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException -= OnDomain;
        TaskScheduler.UnobservedTaskException -= OnTask;
        installed = false;
    }

    private void OnDomain(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Handle(ex, "AppDomain");
        }
    }

    private void OnTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Handle(e.Exception, "TaskScheduler");
        e.SetObserved();
    }
}
