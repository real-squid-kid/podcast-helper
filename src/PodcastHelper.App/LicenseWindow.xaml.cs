using System.Windows;
using PodcastHelper.App.ViewModels;
namespace PodcastHelper.App;
public partial class LicenseWindow : Window
{
    public LicenseWindow(LicenseViewModel vm)
    {
        InitializeComponent(); DialogSizing.Attach(this); DataContext = vm;
        vm.CloseRequested += Close; Closed += (_, _) => vm.CloseRequested -= Close;
    }
}
