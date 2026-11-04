using System.Windows;
using PodcastHelper.App.ViewModels;
using PodcastHelper.Infrastructure.Storage;
using PodcastHelper.Infrastructure.Sessions;
using PodcastHelper.Infrastructure.Time;
using PodcastHelper.Core.Models;
using System.IO;
using System.Windows.Threading;
using PodcastHelper.Infrastructure.Export;

namespace PodcastHelper.App;

public partial class App : Application
{
    private MainViewModel? mainViewModel;
    private bool closing, allowCloseUsingSavedEvents;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = new PortablePaths(ApplicationLocation.RootDirectory);
        var fileSystem = new PhysicalFileSystem();
        var store = new PortableDataStore(paths, new JsonDocumentStore(fileSystem), fileSystem);
        var journalStore = new SessionJournalStore(paths, fileSystem);
        var viewModel = new MainViewModel(store, journalStore, paths, historyStore: new SessionHistoryStore(paths, fileSystem), exporter: new SessionExportService(fileSystem)) { CheckingRecovery = true };
        mainViewModel = viewModel;
        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Closing += OnMainWindowClosing;
        MainWindow.Show();
        await viewModel.InitializeAsync();
        if (!viewModel.IsInitialized) { viewModel.CheckingRecovery = false; return; }
        try
        {
            var sessions = await Task.Run(() => journalStore.DiscoverAsync());
            if (sessions.Any(x => x.Error is not null))
                viewModel.ReportSessionStatus("Some saved sessions are unavailable. Their files were not changed.", advice: "Restore the affected session from your backup, or start a new session.");
            foreach (var saved in sessions.Where(x => x.Error is null && !x.Archived && x.State != SessionState.Finished))
            {
                var controller = await DurableSessionController.OpenAsync(journalStore, saved.SessionId, new SystemClock());
                bool retain = false;
                try
                {
                    var plan = await controller.GetRecoveryPlanAsync();
                    if (!plan.RequiresConfirmation) await controller.RecoverAsync();
                    else
                    {
                        var recovery = new RecoveryViewModel(plan, new SystemClock(), viewModel.Settings!.HasMacropad);
                        ((MainWindow)MainWindow).ShowOperatorDialog(new RecoveryWindow(recovery));
                        if (recovery.StartAnew) { await controller.AbandonAsync(); continue; }
                        if (recovery.CorrectedSession is null)
                        {
                            viewModel.ReportSessionStatus("Recovery deferred. The saved session was not changed.", advice: "Reopen PodcastHelper to review recovery, or start a new session.");
                            return;
                        }
                        await controller.RecoverAsync(recovery.CorrectedSession);
                    }
                    await viewModel.AttachControllerAsync(controller); retain = true;
                    viewModel.ReportSessionStatus("Session recovered using operator time estimates. Review the current recording state before continuing.");
                    return;
                }
                finally { if (!retain) await controller.DisposeAsync(); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDocumentException or UnsupportedSchemaException
            or PodcastHelper.Core.Validation.ModelValidationException or InvalidOperationException)
        { viewModel.ReportSessionStatus("Session recovery could not be completed. Saved files were retained for review.", true, "Check disk space and folder permissions, then reopen PodcastHelper. If the session is damaged, restore its backup."); }
        finally { viewModel.CheckingRecovery = false; }
    }

    private async void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var activeSession = mainViewModel?.Controller;
        if (activeSession is null) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        try
        {
            if (!allowCloseUsingSavedEvents)
            {
                try
                {
                    await activeSession.CheckpointIfDueAsync(force: true);
                    if (activeSession.IsFaulted || activeSession.SnapshotError is not null)
                        throw new IOException("Session storage needs review.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                    or InvalidDocumentException or UnsupportedSchemaException)
                {
                    allowCloseUsingSavedEvents = true;
                    mainViewModel?.ReportSessionStatus("The latest checkpoint could not be saved.", true, "Free disk space and restore write access, or close again to exit using the last saved events.");
                    return;
                }
            }
            await activeSession.DisposeAsync();
            mainViewModel?.DetachAfterExit();
            MainWindow.Close();
        }
        finally { closing = false; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (mainViewModel?.Controller is { } controller) controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
