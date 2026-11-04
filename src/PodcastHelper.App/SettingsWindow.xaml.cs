using System.Windows;
using PodcastHelper.App.ViewModels;

namespace PodcastHelper.App;
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel viewModel;
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent(); DialogSizing.Attach(this); viewModel = vm; DataContext = vm; vm.CloseRequested += Close; Closed += (_, _) => vm.CloseRequested -= Close;
    }
}
