using System.Windows;
using System.Windows.Controls;
using ATM.Presentation.ViewModels;

namespace ATM.UI.Views;

public partial class MaintenanceView : UserControl
{
    public MaintenanceView() => InitializeComponent();

    private void OnSupervisorLoginClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MaintenanceViewModel viewModel)
        {
            viewModel.Passcode = PasscodeBox.Password;
            PasscodeBox.Clear();
            viewModel.EnterSupervisorCommand.Execute(null);
        }
    }
}
