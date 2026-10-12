using System.Windows;
using ATM.Presentation.ViewModels;
using Microsoft.Extensions.Options;

namespace ATM.UI.Shell;

public partial class ShellWindow : Window
{
    public ShellWindow(ShellViewModel viewModel, IOptions<UiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(options);

        InitializeComponent();
        DataContext = viewModel;

        if (options.Value.Kiosk)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Topmost = true;
        }
    }
}
