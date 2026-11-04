using System.Windows;
using PodcastHelper.App.ViewModels;

namespace PodcastHelper.App;

public partial class RecoveryWindow : Window
{
    public RecoveryWindow(RecoveryViewModel viewModel)
    {
        InitializeComponent();
        DialogSizing.Attach(this);
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
        Closed += (_, _) => viewModel.CloseRequested -= Close;
    }
}
