using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace ATM.Presentation.Navigation;

public abstract class ViewModelBase : ObservableObject;

public interface INavigationService : INotifyPropertyChanged
{
    ViewModelBase? Current { get; }

    void NavigateTo<TViewModel>()
        where TViewModel : ViewModelBase;
}

/// <summary>Switches the shell between ViewModels. The UI layer maps each ViewModel type to a View.</summary>
public sealed class NavigationService(IServiceProvider services) : ObservableObject, INavigationService
{
    private ViewModelBase? _current;

    public ViewModelBase? Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    public void NavigateTo<TViewModel>()
        where TViewModel : ViewModelBase
    {
        var next = services.GetRequiredService<TViewModel>();
        var previous = Current;
        Current = next;
        (previous as IDisposable)?.Dispose();
    }
}
