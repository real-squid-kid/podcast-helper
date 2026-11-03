using System.IO;
using PodcastHelper.Core.Models;
using PodcastHelper.Core.Validation;
using PodcastHelper.Infrastructure.Storage;

namespace PodcastHelper.App.ViewModels;

public sealed class MainViewModel(PortableDataStore dataStore) : ObservableObject
{
    private string statusMessage = "Loading settings...";
    private bool isInitialized;

    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public bool IsInitialized { get => isInitialized; private set => SetProperty(ref isInitialized, value); }
    public AppSettings? Settings { get; private set; }
    public string DisplayState => "STANDBY";
    public void ReportSessionStatus(string message) => StatusMessage = message;

    public async Task InitializeAsync()
    {
        try
        {
            // Доступ к диску и JSON не занимают UI-поток, включая синхронную часть файловых API.
            Settings = await Task.Run(() => dataStore.InitializeSettingsAsync());
            IsInitialized = true;
            StatusMessage = "Settings loaded. Session controls are not available in this build.";
        }
        catch (UnsupportedSchemaException)
        {
            StatusMessage = "Settings use an unsupported format version. The file was not changed.";
        }
        catch (Exception ex) when (ex is InvalidDocumentException or ModelValidationException)
        {
            StatusMessage = "Settings are damaged or invalid. The file was not changed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StatusMessage = "Cannot access application data. Use a writable application folder and restart PodcastHelper.";
        }
    }
}
