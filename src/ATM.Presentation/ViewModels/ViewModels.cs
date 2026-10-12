using System.Collections.ObjectModel;
using ATM.Application.Audit;
using ATM.Application.Devices;
using ATM.Application.Health;
using ATM.Application.Notifications;
using ATM.Application.Supervisor;
using ATM.Core.Entities;
using ATM.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ATM.Presentation.ViewModels;

public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    [ObservableProperty]
    private string _banner = string.Empty;

    public ShellViewModel(INavigationService navigation, UiBannerChannel? banner = null)
    {
        _navigation = navigation;
        if (banner is not null)
        {
            banner.Changed += (_, _) =>
            {
                var latest = banner.Current.FirstOrDefault();
                void Apply() => Banner = latest is null ? string.Empty : $"{latest.Title}: {latest.Message}";
                if (_uiContext is null)
                {
                    Apply();
                }
                else
                {
                    _uiContext.Post(_ => Apply(), null);
                }
            };
        }

        _navigation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(INavigationService.Current))
            {
                OnPropertyChanged(nameof(Current));
            }
        };
    }

    public string Title => "ATM Platform";

    public ViewModelBase? Current => _navigation.Current;

    [RelayCommand]
    private void ShowHome() => _navigation.NavigateTo<HomeViewModel>();

    [RelayCommand]
    private void ShowMaintenance() => _navigation.NavigateTo<MaintenanceViewModel>();
}

public sealed class HomeViewModel : ViewModelBase
{
    public string Heading => "Welcome";

    public string Prompt => "Insert your card to begin";
}

public sealed record DeviceHealthItem(string Name, string Status, string Detail);

public sealed record HealthItem(string Name, string State, string Message, string LastRun);

public sealed partial class MaintenanceViewModel(
    IDeviceSelfTestService selfTest,
    ISupervisorSession? supervisor = null,
    HealthMonitor? health = null,
    IAuditService? audit = null) : ViewModelBase, IDisposable
{
    public static readonly TimeSpan HealthRefreshInterval = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? _refreshLoop;

    [ObservableProperty]
    private string _overall = "Not run";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isSupervisor;

    [ObservableProperty]
    private string _supervisorMessage = string.Empty;

    [ObservableProperty]
    private string _healthOverall = "Not run";

    /// <summary>Set by the view from its password box; cleared after every attempt.</summary>
    public string Passcode { get; set; } = string.Empty;

    public bool CanUseSupervisorMode => supervisor is not null;

    public ObservableCollection<DeviceHealthItem> Devices { get; } = [];

    public ObservableCollection<HealthItem> Health { get; } = [];

    [RelayCommand]
    private async Task RunSelfTestAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        try
        {
            var report = await selfTest.RunAsync(cancellationToken);
            Devices.Clear();
            foreach (var device in report.Devices)
            {
                Devices.Add(new DeviceHealthItem(device.Kind.ToString(), device.Status.ToString(), device.Detail));
            }

            Overall = report.Overall.ToString();
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task EnterSupervisorAsync()
    {
        if (supervisor is null)
        {
            SupervisorMessage = "Supervisor mode is not available.";
            return;
        }

        var result = supervisor.TryEnter(Passcode);
        Passcode = string.Empty;
        if (audit is not null)
        {
            await audit.RecordAsync("operator", "SupervisorLogin", "maintenance", result.ToString(), AuditCategory.Security);
        }

        IsSupervisor = supervisor.IsActive;
        SupervisorMessage = result switch
        {
            SupervisorResult.Granted => string.Empty,
            SupervisorResult.Denied => "Wrong passcode.",
            SupervisorResult.LockedOut => "Too many attempts. Try again in a minute.",
            _ => "Supervisor mode is not configured on this terminal.",
        };

        if (IsSupervisor)
        {
            await RefreshHealthAsync();
            StartAutoRefresh();
        }
    }

    [RelayCommand]
    private void ExitSupervisor()
    {
        StopAutoRefresh();
        supervisor?.Exit();
        IsSupervisor = false;
        Health.Clear();
        HealthOverall = "Not run";
    }

    [RelayCommand]
    private async Task RefreshHealthAsync()
    {
        if (health is null || supervisor is not { IsActive: true })
        {
            return;
        }

        var report = await health.RunAsync();
        Health.Clear();
        foreach (var entry in report.Entries)
        {
            Health.Add(new HealthItem(entry.Name, entry.State.ToString(), entry.Message, entry.LastRunUtc.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)));
        }

        HealthOverall = report.Overall.ToString();
    }

    /// <summary>Refreshes the health list every 30 seconds while supervisor mode is active. Runs on the caller's context, so the UI thread updates the list.</summary>
    private void StartAutoRefresh()
    {
        StopAutoRefresh();
        _refreshLoop = new CancellationTokenSource();
        _ = LoopAsync(_refreshLoop.Token);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HealthRefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshHealthAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // leaving supervisor mode or the screen
        }
    }

    private void StopAutoRefresh()
    {
        _refreshLoop?.Cancel();
        _refreshLoop?.Dispose();
        _refreshLoop = null;
    }

    public void Dispose()
    {
        StopAutoRefresh();
        supervisor?.Exit();
    }
}
