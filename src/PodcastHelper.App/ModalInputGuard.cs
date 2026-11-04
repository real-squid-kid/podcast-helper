using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PodcastHelper.App.ViewModels;

namespace PodcastHelper.App;

internal static class ModalInputGuard
{
    public static void Attach(Window window)
    {
        window.PreviewKeyDown += (_, e) =>
        {
            e.Handled = !AllowsKey(e.Key, Keyboard.Modifiers, Keyboard.FocusedElement is TextBoxBase,
                Keyboard.FocusedElement is TextBox { AcceptsReturn: true }, window.DataContext is PromptViewModel { ClapOnly: true }, e.IsRepeat);
        };
        window.PreviewKeyUp += (_, e) => { if (Keyboard.FocusedElement is not TextBoxBase) e.Handled = true; };
        window.PreviewTextInput += (_, e) => { if (Keyboard.FocusedElement is not TextBoxBase) e.Handled = true; };
    }
    private static bool AllowsKey(Key key, ModifierKeys modifiers, bool textField, bool multiline, bool clap, bool repeat)
    {
        if (clap && key == Key.Enter && !repeat && modifiers == ModifierKeys.None) return true;
        if (!textField) return false;
        bool editingShortcut = (modifiers & ModifierKeys.Control) != 0 && (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) == 0
            && key is Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Back or Key.Delete;
        bool plainText = (modifiers & (ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Windows)) == 0
            && key is not (Key.Tab or Key.Escape or Key.System or Key.Apps) && (key < Key.F1 || key > Key.F24)
            && (key != Key.Enter || multiline);
        return editingShortcut || plainText;
    }
}
