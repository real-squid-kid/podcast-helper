using System.Windows.Input;

namespace PodcastHelper.App.ViewModels;

public sealed class AboutViewModel : ObservableObject
{
    private string message = "";
    public string Version { get; }
    public string Build { get; }
    public bool ShowMouseHint { get; }
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public Action<Uri>? OpenLink { get; set; }
    public Func<bool, Task>? ShowLicenses { get; set; }
    public Action<string>? CopyText { get; set; }
    public Func<string>? DiagnosticInfo { get; set; }
    public ICommand MitCommand { get; }
    public ICommand LicenseCommand { get; }
    public ICommand ThirdPartyCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand GitHubCommand { get; }
    public ICommand CloseCommand { get; }
    public event Action? CloseRequested;
    public AboutViewModel(string version, string build, bool mouse, Uri? repository = null)
    {
        Version = version; Build = build; ShowMouseHint = mouse;
        AsyncRelayCommand Action(Func<Task> action, Func<bool>? allowed = null) => new(async () =>
        {
            try { Message = ""; await action(); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { Message = "Could not open this item. Restore the application files, or check your default browser and try again."; }
        }, allowed);
        MitCommand = Action(() => { OpenLink?.Invoke(new Uri("https://opensource.org/license/mit")); return Task.CompletedTask; });
        LicenseCommand = Action(async () => { if (ShowLicenses is not null) await ShowLicenses(false); });
        ThirdPartyCommand = Action(async () => { if (ShowLicenses is not null) await ShowLicenses(true); });
        CopyCommand = new RelayCommand(() =>
        {
            try { if (DiagnosticInfo is null || CopyText is null) return; CopyText(DiagnosticInfo()); Message = "Diagnostic info copied"; }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            { Message = "Clipboard is busy. Try Copy diagnostic info again."; }
        });
        GitHubCommand = Action(() =>
        {
            if (repository is { IsAbsoluteUri: true, Scheme: "https" }) OpenLink?.Invoke(repository);
            return Task.CompletedTask;
        }, () => repository is { IsAbsoluteUri: true, Scheme: "https" });
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }
}

public sealed record LicenseItem(string Name, string Version, string Owner, string File);
public sealed class LicenseViewModel : ObservableObject
{
    private readonly Func<string, Task<string>> read;
    private LicenseItem? selected;
    private string text = "", message = "";
    private int version;
    public IReadOnlyList<LicenseItem> Items { get; }
    public bool HasChoices => Items.Count > 1;
    public bool ShowMouseHint { get; }
    public string Heading { get; }
    public string Text { get => text; private set => SetProperty(ref text, value); }
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public LicenseItem? Selected { get => selected; set { if (SetProperty(ref selected, value)) Loading = LoadAsync(value); } }
    public Task Loading { get; private set; } = Task.CompletedTask;
    public ICommand CloseCommand { get; }
    public event Action? CloseRequested;
    public LicenseViewModel(string heading, IReadOnlyList<LicenseItem> items, bool mouse, Func<string, Task<string>> read)
    {
        Heading = heading; Items = items; ShowMouseHint = mouse; this.read = read;
        CloseCommand = new RelayCommand(() => { version++; CloseRequested?.Invoke(); });
        Selected = Items.FirstOrDefault();
    }
    private async Task LoadAsync(LicenseItem? item)
    {
        int current = ++version; Text = ""; Message = ""; if (item is null) return;
        try { string content = await read(item.File); if (current == version) Text = content; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { if (current == version) Message = "License file is unavailable. Restore the application files from the portable package."; }
    }
}
