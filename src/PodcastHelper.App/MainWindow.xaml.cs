using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using PodcastHelper.App.ViewModels;
using PodcastHelper.Infrastructure.Storage;

namespace PodcastHelper.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private MainViewModel? viewModel;
    private readonly TimerSoundPlayer sound;
    private readonly MacropadInput macropad;
    private readonly FocusAttention? focusAttention;
    private int modalDepth;
    private bool fullScreen;
    public MainWindow()
    {
        InitializeComponent();
        macropad = new MacropadInput(this, () => viewModel, () => modalDepth > 0);
        if (Application.Current is { } application) focusAttention = new FocusAttention(application, this);
        sound = new TimerSoundPlayer(new PortablePaths(AppContext.BaseDirectory), message => viewModel?.ReportSessionStatus(message));
        DataContextChanged += OnContextChanged;
        refreshTimer.Tick += async (_, _) => { if (viewModel is not null) await viewModel.PollAsync(); };
        Loaded += (_, _) => { refreshTimer.Start(); SetFullScreen(true); };
        Closed += (_, _) => { refreshTimer.Stop(); sound.Close(); macropad.Dispose(); focusAttention?.Dispose(); };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (viewModel is not null) { viewModel.FullScreenRequested -= ToggleFullScreen; viewModel.TimerEnded -= sound.Play; viewModel.UndoApplied -= FlashUndo; }
        viewModel = e.NewValue as MainViewModel; if (viewModel is null) return;
        viewModel.SystemAnimationsEnabled = SystemParameters.ClientAreaAnimation;
        viewModel.RequestPrompt = vm => { ShowOperatorDialog(new PromptWindow(vm)); return Task.FromResult(vm.Selected); };
        viewModel.RequestEditor = vm => { ShowOperatorDialog(new ScenarioEditorWindow(vm)); return Task.FromResult(vm.Result); };
        viewModel.RequestSettings = vm => { ShowOperatorDialog(new SettingsWindow(vm)); return Task.FromResult(vm.Result); };
        viewModel.RequestHistory = vm => { ShowOperatorDialog(new HistoryWindow(vm)); return Task.CompletedTask; };
        viewModel.RequestExportFolder = folder => HistoryWindow.ChooseFolderAsync(this, folder, viewModel.HasMacropad);
        viewModel.OpenExportFolder = HistoryWindow.OpenExportFolder;
        viewModel.FullScreenRequested += ToggleFullScreen; viewModel.TimerEnded += sound.Play; viewModel.UndoApplied += FlashUndo;
    }
    internal void ShowOperatorDialog(Window dialog)
    {
        modalDepth++;
        try { dialog.Owner = this; dialog.ShowDialog(); }
        finally { modalDepth--; }
    }
    private void FlashUndo()
    {
        var flash = new DoubleAnimationUsingKeyFrames();
        flash.KeyFrames.Add(new DiscreteDoubleKeyFrame(viewModel?.ReduceAnimations == true ? .12 : .35, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(viewModel?.ReduceAnimations == true ? 120 : 380))));
        UndoFlash.BeginAnimation(OpacityProperty, flash);
    }
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsActive || !IsEnabled || modalDepth > 0) return;
        if (e.Key == Key.Escape && fullScreen) { SetFullScreen(false); e.Handled = true; return; }
        if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; return; }
        if (e.Key == Key.Enter && !e.IsRepeat && Keyboard.Modifiers == ModifierKeys.None && viewModel is not null
            && Keyboard.FocusedElement is not (TextBoxBase or ComboBox) && viewModel.CurrentSession?.State is PodcastHelper.Core.Models.SessionState.Ready or PodcastHelper.Core.Models.SessionState.AwaitingClap)
        { e.Handled = true; await viewModel.PhysicalEnterAsync(); }
    }
    private async void OnWrapUpMouse(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && viewModel is not null) { e.Handled = true; await viewModel.WrapUpFromMouseAsync(); }
    }
    private void ToggleFullScreen() => SetFullScreen(!fullScreen);
    private void SetFullScreen(bool enabled)
    {
        fullScreen = enabled; WindowState = WindowState.Normal; WindowStyle = enabled ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = enabled ? WindowState.Maximized : WindowState.Normal;
        if (!enabled)
        {
            var available = DialogSizing.AvailableSize(this);
            Width = Math.Min(Width, available.Width); Height = Math.Min(Height, available.Height);
        }
    }
}
