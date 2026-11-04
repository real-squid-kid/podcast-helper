using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using PodcastHelper.App.ViewModels;

namespace PodcastHelper.App;
public partial class FolderWindow : Window
{
    private readonly FolderViewModel viewModel;
    public FolderWindow(FolderViewModel vm)
    {
        InitializeComponent(); DialogSizing.Attach(this); viewModel = vm; DataContext = vm;
        vm.CloseRequested += Close; Closing += (_, e) => e.Cancel = vm.IsBusy; Closed += (_, _) => vm.CloseRequested -= Close;
    }
    private void OpenFolder(object sender, MouseButtonEventArgs e)
    { if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject source
        && ItemsControl.ContainerFromElement(FolderList, source) is ListBoxItem && viewModel.OpenCommand.CanExecute(null)) viewModel.OpenCommand.Execute(null); }
}
