using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace PodcastHelper.App.ViewModels;

public sealed record FolderItem(string Name, string Path);
public sealed class FolderViewModel : ObservableObject
{
    private string inputPath, currentPath = "", error = "";
    private bool busy;
    private FolderItem? selected;
    private readonly Func<string, Task<IReadOnlyList<FolderItem>>> list;
    private readonly List<AsyncRelayCommand> commands = [];
    public bool ShowMouseHint { get; }
    public string InputPath { get => inputPath; set => SetProperty(ref inputPath, value); }
    public string CurrentPath { get => currentPath; private set { if (SetProperty(ref currentPath, value)) Notify(nameof(CanChoose)); } }
    public string Error { get => error; private set => SetProperty(ref error, value); }
    public bool IsBusy { get => busy; private set { if (SetProperty(ref busy, value)) { Notify(nameof(NotBusy)); Notify(nameof(CanChoose)); foreach (var command in commands) command.Refresh(); } } }
    public bool NotBusy => !IsBusy;
    public FolderItem? Selected { get => selected; set { if (SetProperty(ref selected, value)) Notify(nameof(CanChoose)); } }
    public bool CanChoose => !IsBusy && (Selected is not null || CurrentPath.Length > 0);
    public ObservableCollection<FolderItem> Folders { get; } = [];
    public string? Result { get; private set; }
    public ICommand GoCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand DrivesCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand ChooseCommand { get; }
    public ICommand CancelCommand { get; }
    public event Action? CloseRequested;
    public FolderViewModel(string initialPath, bool mouse, Func<string, Task<IReadOnlyList<FolderItem>>>? list = null)
    {
        inputPath = initialPath; ShowMouseHint = mouse; this.list = list ?? ListPhysicalAsync;
        GoCommand = Make(() => NavigateAsync(InputPath));
        UpCommand = Make(() => NavigateAsync(Directory.GetParent(CurrentPath)?.FullName ?? ""));
        DrivesCommand = Make(() => NavigateAsync(""));
        OpenCommand = Make(async () => { if (Selected is not null) await NavigateAsync(Selected.Path); });
        ChooseCommand = new RelayCommand(() => { if (!IsBusy && (Selected is not null || CurrentPath.Length > 0)) { Result = Selected?.Path ?? CurrentPath; CloseRequested?.Invoke(); } });
        CancelCommand = new RelayCommand(() => { if (!IsBusy) CloseRequested?.Invoke(); });
    }
    private AsyncRelayCommand Make(Func<Task> action)
    { var command = new AsyncRelayCommand(async () => { IsBusy = true; try { await action(); } finally { IsBusy = false; } }, () => !IsBusy); commands.Add(command); return command; }
    public async Task NavigateAsync(string path)
    {
        try
        {
            Error = ""; string normalized = path.Length == 0 ? "" : Path.GetFullPath(path);
            var items = await list(normalized); Folders.Clear(); Selected = null; Notify(nameof(Selected));
            foreach (var item in items) Folders.Add(item);
            CurrentPath = normalized; InputPath = normalized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { InputPath = CurrentPath; Error = "Cannot open this folder. Choose another folder, or check its location and access permissions."; }
    }
    public static Task<IReadOnlyList<FolderItem>> ListPhysicalAsync(string path) => Task.Run<IReadOnlyList<FolderItem>>(() => path.Length == 0
        ? Directory.GetLogicalDrives().Select(x => new FolderItem(x, x)).ToArray()
        : Directory.GetDirectories(path).Select(x => new FolderItem(Path.GetFileName(x), x)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
}
