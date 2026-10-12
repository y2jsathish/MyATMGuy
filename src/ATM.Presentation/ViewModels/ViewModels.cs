using System.Collections.ObjectModel;
using ATM.Application.Devices;
using ATM.Presentation.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ATM.Presentation.ViewModels;

public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;

    public ShellViewModel(INavigationService navigation)
    {
        _navigation = navigation;
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

public sealed partial class MaintenanceViewModel(IDeviceSelfTestService selfTest) : ViewModelBase
{
    [ObservableProperty]
    private string _overall = "Not run";

    [ObservableProperty]
    private bool _isRunning;

    public ObservableCollection<DeviceHealthItem> Devices { get; } = [];

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
}
