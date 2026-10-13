using ATM.Application.Masking;
using ATM.Core.Errors;

namespace ATM.Application.Notifications;

public sealed record Notification(ErrorSeverity Severity, string Title, string Message, DateTimeOffset CreatedUtc);

public interface INotificationChannel
{
    string Name { get; }

    Task SendAsync(Notification notification, CancellationToken cancellationToken = default);
}

public interface INotificationService
{
    Task NotifyAsync(ErrorSeverity severity, string title, string message, CancellationToken cancellationToken = default);
}

/// <summary>Which channels receive which severities. A channel receives a notification when severity is at or above its minimum.</summary>
public sealed class NotificationOptions
{
    public Dictionary<string, ErrorSeverity> MinimumSeverityByChannel { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["log"] = ErrorSeverity.Low,
        ["ui"] = ErrorSeverity.High,
        ["monitoring"] = ErrorSeverity.Medium,
    };
}

public sealed class NotificationService(IEnumerable<INotificationChannel> channels, NotificationOptions options, TimeProvider clock) : INotificationService
{
    private readonly IReadOnlyList<INotificationChannel> all = [.. channels];

    public async Task NotifyAsync(ErrorSeverity severity, string title, string message, CancellationToken cancellationToken = default)
    {
        var notification = new Notification(severity, SensitiveDataMasker.Mask(title), SensitiveDataMasker.Mask(message), clock.GetUtcNow());
        foreach (var channel in all)
        {
            if (options.MinimumSeverityByChannel.TryGetValue(channel.Name, out var minimum) && severity >= minimum)
            {
                try
                {
                    await channel.SendAsync(notification, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One broken channel must not stop the others.
                }
            }
        }
    }
}

/// <summary>Keeps the newest notifications for the UI banner.</summary>
public sealed class UiBannerChannel : INotificationChannel
{
    private readonly object sync = new();
    private readonly List<Notification> items = [];

    public string Name => "ui";

    public event EventHandler? Changed;

    public IReadOnlyList<Notification> Current
    {
        get
        {
            lock (sync)
            {
                return [.. items];
            }
        }
    }

    public Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            items.Insert(0, notification);
            if (items.Count > 20)
            {
                items.RemoveAt(items.Count - 1);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void Clear()
    {
        lock (sync)
        {
            items.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
