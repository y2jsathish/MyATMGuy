using System.Collections.ObjectModel;
using ATM.Application.Audit;
using ATM.Application.Devices;
using ATM.Application.Health;
using ATM.Application.Notifications;
using ATM.Application.Supervisor;
using ATM.Application.Transactions;
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
    private void ShowServices() => _navigation.NavigateTo<CustomerServiceViewModel>();

    [RelayCommand]
    private void ShowMaintenance() => _navigation.NavigateTo<MaintenanceViewModel>();
}

public sealed class HomeViewModel : ViewModelBase
{
    public string Heading => "Welcome";

    public string Prompt => "Insert your card to begin";
}

public enum CustomerStage
{
    Choose,
    Working,
    Result,
}

/// <summary>
/// The customer screen: pick a service, follow the device prompts while it runs, read the result. The result goes away by itself after a
/// short time so the next person never sees the last customer's balance.
/// </summary>
public sealed partial class CustomerServiceViewModel(ICustomerSessionService? sessions = null, TimeSpan? resultTimeout = null) : ViewModelBase, IDisposable
{
    public static readonly TimeSpan DefaultResultTimeout = TimeSpan.FromSeconds(20);

    private CancellationTokenSource? _run;
    private CancellationTokenSource? _returnTimer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(IsWorking), nameof(IsResult))]
    private CustomerStage _stage = CustomerStage.Choose;

    [ObservableProperty]
    private string _prompt = "Choose a service";

    [ObservableProperty]
    private string _resultTitle = string.Empty;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private bool _isSuccess;

    public bool IsAvailable => sessions is not null;

    public bool IsChoosing => Stage == CustomerStage.Choose;

    public bool IsWorking => Stage == CustomerStage.Working;

    public bool IsResult => Stage == CustomerStage.Result;

    public IReadOnlyList<decimal> QuickAmounts => sessions?.QuickAmounts ?? [];

    public ObservableCollection<string> ResultLines { get; } = [];

    [RelayCommand]
    private Task WithdrawAsync(decimal amount) => RunAsync("Insert your card, then enter your PIN", ct => sessions!.WithdrawAsync(amount, ct));

    [RelayCommand]
    private Task BalanceAsync() => RunAsync("Insert your card, then enter your PIN", ct => sessions!.BalanceAsync(ct));

    [RelayCommand]
    private Task MiniStatementAsync() => RunAsync("Insert your card, then enter your PIN", ct => sessions!.MiniStatementAsync(ct));

    [RelayCommand]
    private Task ChangePinAsync() => RunAsync("Insert your card, enter your current PIN, then the new PIN twice", ct => sessions!.ChangePinAsync(ct));

    /// <summary>Stops a running service. The flow returns the card on its way out.</summary>
    [RelayCommand]
    private void Cancel() => _run?.Cancel();

    /// <summary>Leaves the result screen for the service menu.</summary>
    [RelayCommand]
    private void Done() => ReturnToMenu();

    private async Task RunAsync(string prompt, Func<CancellationToken, Task<CustomerSessionResult>> service)
    {
        if (sessions is null || Stage == CustomerStage.Working)
        {
            return;
        }

        StopReturnTimer();
        _run = new CancellationTokenSource();
        Prompt = prompt;
        Stage = CustomerStage.Working;
        CustomerSessionResult result;
        try
        {
            result = await service(_run.Token);
        }
        catch (OperationCanceledException)
        {
            ReturnToMenu();
            return;
        }
        catch (Exception)
        {
            result = new CustomerSessionResult(false, "Transaction not completed", "This ATM cannot complete your request.", []);
        }
        finally
        {
            _run.Dispose();
            _run = null;
        }

        ResultTitle = result.Title;
        ResultMessage = result.Message;
        IsSuccess = result.Succeeded;
        ResultLines.Clear();
        foreach (var line in result.Lines)
        {
            ResultLines.Add(line);
        }

        Stage = CustomerStage.Result;
        StartReturnTimer();
    }

    private void StartReturnTimer()
    {
        _returnTimer = new CancellationTokenSource();
        _ = ReturnAfterAsync(resultTimeout ?? DefaultResultTimeout, _returnTimer.Token);
    }

    private async Task ReturnAfterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            ReturnToMenu();
        }
        catch (OperationCanceledException)
        {
            // the customer pressed Done, or started another service
        }
    }

    private void ReturnToMenu()
    {
        StopReturnTimer();
        ResultLines.Clear();
        ResultTitle = string.Empty;
        ResultMessage = string.Empty;
        Prompt = "Choose a service";
        Stage = CustomerStage.Choose;
    }

    private void StopReturnTimer()
    {
        _returnTimer?.Cancel();
        _returnTimer?.Dispose();
        _returnTimer = null;
    }

    public void Dispose()
    {
        _run?.Cancel();
        StopReturnTimer();
    }
}

public sealed record DeviceHealthItem(string Name, string Status, string Detail);

public sealed record HealthItem(string Name, string State, string Message, string LastRun);

public sealed record ReversalItem(string Transaction, string Amount, string State, int Attempts, string Created, string LastError);

public sealed partial class MaintenanceViewModel(
    IDeviceSelfTestService selfTest,
    ISupervisorSession? supervisor = null,
    HealthMonitor? health = null,
    IAuditService? audit = null,
    IReversalReport? reversalReport = null) : ViewModelBase, IDisposable
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

    /// <summary>Reversals the host has not accepted yet or that were given up on; shown to supervisors for manual reconciliation.</summary>
    public ObservableCollection<ReversalItem> Reversals { get; } = [];

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
        Reversals.Clear();
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
        await RefreshReversalsAsync();
    }

    private async Task RefreshReversalsAsync()
    {
        if (reversalReport is null)
        {
            return;
        }

        var unresolved = await reversalReport.UnresolvedAsync();
        Reversals.Clear();
        foreach (var r in unresolved)
        {
            Reversals.Add(new ReversalItem(
                r.Id.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                r.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                r.State.ToString(),
                r.Attempts,
                r.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                r.LastError ?? string.Empty));
        }
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
