using System.Windows;
using PodcastHelper.App.ViewModels;
using PodcastHelper.Infrastructure.Storage;
using PodcastHelper.Infrastructure.Sessions;
using PodcastHelper.Infrastructure.Time;
using PodcastHelper.Core.Models;
using System.IO;
using System.Windows.Threading;

namespace PodcastHelper.App;

public partial class App : Application
{
    private DurableSessionController? activeSession;
    private DispatcherTimer? storageHealthTimer;
    private MainViewModel? mainViewModel;
    private bool closing, allowCloseUsingSavedEvents;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = new PortablePaths(AppContext.BaseDirectory);
        var store = new PortableDataStore(paths, new JsonDocumentStore(new PhysicalFileSystem()));
        var viewModel = new MainViewModel(store);
        mainViewModel = viewModel;
        MainWindow = new MainWindow { DataContext = viewModel };
        MainWindow.Closing += OnMainWindowClosing;
        MainWindow.Show();
        await viewModel.InitializeAsync();
        if (!viewModel.IsInitialized) return;
        try
        {
            var journalStore = new SessionJournalStore(paths, new PhysicalFileSystem());
            var sessions = await Task.Run(() => journalStore.DiscoverAsync());
            if (sessions.Any(x => x.Error is not null))
                viewModel.ReportSessionStatus("Some saved sessions are unavailable. Their files were not changed.");
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
                        new RecoveryWindow(recovery) { Owner = MainWindow }.ShowDialog();
                        if (recovery.StartAnew) { await controller.AbandonAsync(); continue; }
                        if (recovery.CorrectedSession is null)
                        {
                            viewModel.ReportSessionStatus("Recovery deferred. The saved session was not changed.");
                            return;
                        }
                        await controller.RecoverAsync(recovery.CorrectedSession);
                    }
                    activeSession = controller; retain = true;
                    viewModel.ReportSessionStatus("Session recovered using operator time estimates. Session controls are not available in this build.");
                    storageHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    storageHealthTimer.Tick += (_, _) =>
                    {
                        if (controller.IsFaulted || controller.SnapshotError is not null || controller.BackgroundError is not null)
                            viewModel.ReportSessionStatus("Session saving failed. Resolve storage access before continuing.");
                    };
                    storageHealthTimer.Start();
                    return;
                }
                finally { if (!retain) await controller.DisposeAsync(); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDocumentException or UnsupportedSchemaException
            or PodcastHelper.Core.Validation.ModelValidationException or InvalidOperationException)
        { viewModel.ReportSessionStatus("Session recovery could not be completed. Saved files were retained for review."); }
    }

    private async void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (activeSession is null) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        storageHealthTimer?.Stop();
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
                    mainViewModel?.ReportSessionStatus("The latest checkpoint could not be saved. Close again to exit using the last saved events.");
                    return;
                }
            }
            await activeSession.DisposeAsync();
            activeSession = null;
            MainWindow.Close();
        }
        finally { closing = false; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        storageHealthTimer?.Stop();
        if (activeSession is not null) activeSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
