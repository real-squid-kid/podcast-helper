using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using PodcastHelper.App.ViewModels;

namespace PodcastHelper.App;

// Только сообщения собственного HWND. Глобальные hooks и RegisterHotKey не используются.
internal sealed class MacropadInput : IDisposable
{
    private readonly Window window;
    private readonly Func<MainViewModel?> getViewModel;
    private readonly Func<bool> modalOpen;
    private nint handle;
    private bool attached;

    public MacropadInput(Window window, Func<MainViewModel?> getViewModel, Func<bool> modalOpen)
    {
        this.window = window; this.getViewModel = getViewModel; this.modalOpen = modalOpen;
        window.SourceInitialized += Initialize;
    }

    private void Initialize(object? sender, EventArgs e)
    {
        handle = new WindowInteropHelper(window).Handle;
        // До WPF TranslateAccelerator: иначе сфокусированный контрол может поглотить клавишу.
        ComponentDispatcher.ThreadFilterMessage += OnMessage; attached = true;
    }

    private void OnMessage(ref MSG message, ref bool handled)
    {
        if (handled || message.hwnd != handle || message.message is not (0x0100 or 0x0104)) return;
        var binding = MacropadInputPolicy.Resolve(message.message, message.lParam.ToInt64(), Down(0x11), Down(0x12), Down(0x10), Down(0x5B) || Down(0x5C));
        var vm = getViewModel();
        if (binding is null || vm?.HasMacropad != true) return;
        // Даже отключённая/повторная команда не должна стать обычным нажатием кнопки WPF.
        handled = true;
        var state = new MacropadInputState(vm.HasMacropad, window.IsActive, window.IsEnabled,
            GetForegroundWindow() == handle, modalOpen() || ComponentDispatcher.IsThreadModal, IsEditingInput(Keyboard.FocusedElement),
            vm.IsBusy, vm.CheckingRecovery);
        if (MacropadInputPolicy.ShouldExecute(state, message.lParam.ToInt64())) Dispatch(vm, binding.Action);
    }

    private static async void Dispatch(MainViewModel vm, MacropadAction action) => await vm.ExecuteMacropadAsync(action);

    private static bool IsEditingInput(IInputElement? focused) => focused is TextBoxBase { IsReadOnly: false }
        or PasswordBox or ComboBox { IsEditable: true } or ComboBox { IsDropDownOpen: true };

    private static bool Down(int key) => (GetKeyState(key) & 0x8000) != 0;
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    public void Dispose()
    {
        window.SourceInitialized -= Initialize;
        if (attached) { ComponentDispatcher.ThreadFilterMessage -= OnMessage; attached = false; }
    }
}
