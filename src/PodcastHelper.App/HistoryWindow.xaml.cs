using System.Diagnostics;
using System.IO;
using System.Windows;
using PodcastHelper.App.ViewModels;

namespace PodcastHelper.App;
public partial class HistoryWindow : Window
{
    public HistoryWindow(HistoryViewModel vm)
    {
        InitializeComponent(); DialogSizing.Attach(this); DataContext = vm;
        vm.RequestPrompt = prompt => { new PromptWindow(prompt) { Owner = this }.ShowDialog(); return Task.FromResult(prompt.Selected); };
        vm.RequestFolder = folder => ChooseFolderAsync(this, folder, vm.ShowMouseHint);
        vm.OpenFolder = folder => OpenExportFolder(folder);
        vm.CloseRequested += Close; Closing += (_, e) => e.Cancel = vm.IsBusy; Closed += (_, _) => vm.CloseRequested -= Close;
    }
    internal static async Task<string?> ChooseFolderAsync(Window owner, string folder, bool mouse)
    {
        var vm = new FolderViewModel(folder, mouse);
        await vm.NavigateAsync(Directory.Exists(folder) ? folder : Directory.GetParent(folder)?.FullName ?? "");
        new FolderWindow(vm) { Owner = owner }.ShowDialog(); return vm.Result;
    }
    internal static void OpenExportFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Export folder does not exist yet.");
        Process.Start(new ProcessStartInfo(Path.GetFullPath(folder)) { UseShellExecute = true });
    }
}
