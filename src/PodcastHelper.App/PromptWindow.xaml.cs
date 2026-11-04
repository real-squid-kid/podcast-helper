using System.Windows;
using System.Windows.Input;
using PodcastHelper.App.ViewModels;

namespace PodcastHelper.App;

public partial class PromptWindow : Window
{
    private readonly PromptViewModel viewModel;
    public PromptWindow(PromptViewModel viewModel)
    {
        InitializeComponent(); DialogSizing.Attach(this); this.viewModel = viewModel; DataContext = viewModel;
        viewModel.CloseRequested += Close;
        Closed += (_, _) => viewModel.CloseRequested -= Close;
        PreviewKeyDown += OnPreviewKeyDown;
    }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (viewModel.ClapOnly && e.Key == Key.Enter && !e.IsRepeat && Keyboard.Modifiers == ModifierKeys.None)
        { e.Handled = true; viewModel.PhysicalEnter(); }
    }
}
