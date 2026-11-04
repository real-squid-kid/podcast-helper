using System.Collections.Immutable;

namespace PodcastHelper.App.ViewModels;

public enum MacropadAction
{
    Marker, Funny, Chemistry, Clip, Review,
    TimerPreset1, TimerPreset2, TimerPreset3, TimerStop, Pause,
    NextSegment, Undo, OpenClap, Unassigned
}

public sealed record MacropadBinding(MacropadAction Action, int ScanCode, string Key, string Label, string Icon, string? CategoryId = null, string AssetName = "")
{
    public bool IsAssigned => Action != MacropadAction.Unassigned;
    public string Shortcut => IsAssigned ? $"Ctrl+Alt+Shift+{Key}" : "";
    public string ImagePath => $"/PodcastHelper;component/buttons/{AssetName}.png";
}

// Единственный источник раскладки: обработчик, рисунок и список сочетаний используют эти записи.
public static class MacropadLayout
{
    public static ImmutableArray<MacropadBinding> Bindings { get; } =
    [
        new(MacropadAction.NextSegment, 0x02, "1", "Next segment", "▶|", AssetName: "01-next-segment"),
        new(MacropadAction.Undo, 0x03, "2", "Undo", "↶", AssetName: "02-undo"),
        new(MacropadAction.OpenClap, 0x04, "3", "Open clap", "▰", AssetName: "03-clap"),
        new(MacropadAction.Pause, 0x05, "4", "Pause / Resume", "Ⅱ / ▶", AssetName: "04-pause-resume"),
        new(MacropadAction.Unassigned, 0x06, "5", "BACK", "", AssetName: "05-back"),
        new(MacropadAction.TimerPreset1, 0x07, "6", "Timer 1", "▶", AssetName: "06-timer-1"),
        new(MacropadAction.TimerPreset2, 0x08, "7", "Timer 2", "▶", AssetName: "07-timer-2"),
        new(MacropadAction.TimerPreset3, 0x09, "8", "Timer 3", "▶", AssetName: "08-timer-3"),
        new(MacropadAction.TimerStop, 0x0A, "9", "Stop timer", "■", AssetName: "09-stop-timer"),
        new(MacropadAction.Unassigned, 0x0B, "0", "", "", AssetName: "10-blank"),
        new(MacropadAction.Marker, 0x10, "Q", "Marker", "◇", "marker", "11-marker"),
        new(MacropadAction.Funny, 0x11, "W", "Funny", "✦", "funny", "12-funny"),
        new(MacropadAction.Chemistry, 0x12, "E", "Chemistry", "↔", "chemistry", "13-chemistry"),
        new(MacropadAction.Clip, 0x13, "R", "Clip", "▣", "clip", "14-clip"),
        new(MacropadAction.Review, 0x14, "T", "Review", "◎", "review", "15-review")
    ];

    public static MacropadBinding? Find(int scanCode, bool extended) =>
        extended ? null : Bindings.FirstOrDefault(x => x.IsAssigned && x.ScanCode == scanCode);
}

public readonly record struct MacropadInputState(bool Enabled, bool MainActive, bool MainEnabled, bool Foreground,
    bool ModalOpen, bool Editing, bool Busy, bool Recovering)
{
    public bool AllowsCommand => Enabled && MainActive && MainEnabled && Foreground && !ModalOpen && !Editing && !Busy && !Recovering;
}

public static class MacropadInputPolicy
{
    public static MacropadBinding? Resolve(int message, long keyData, bool control, bool alt, bool shift, bool windows)
    {
        if (message is not (0x0100 or 0x0104) || !control || !alt || !shift || windows) return null;
        return MacropadLayout.Find((int)((keyData >> 16) & 0xFF), (keyData & (1L << 24)) != 0);
    }

    public static bool ShouldExecute(MacropadInputState state, long keyData) =>
        state.AllowsCommand && (keyData & ((1L << 30) | (1L << 31))) == 0;
}
