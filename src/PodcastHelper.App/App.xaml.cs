using System.Windows;
using PodcastHelper.App.ViewModels;
using PodcastHelper.Infrastructure.Storage;

namespace PodcastHelper.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = new PortablePaths(AppContext.BaseDirectory);
        var store = new PortableDataStore(paths, new JsonDocumentStore(new PhysicalFileSystem()));
        var viewModel = new MainViewModel(store);
        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Show();
        await viewModel.InitializeAsync();
    }
}
