using System.Collections.Immutable;
using System.Globalization;
using System.Windows.Input;

namespace PodcastHelper.App.ViewModels;

public sealed record PromptChoice(string Label, ICommand Command)
{
    public string Title { get; init; } = Label;
    public string Description { get; init; } = "";
    public string Icon { get; init; } = "check";
    public bool IsPrimary { get; init; }
}
public sealed class PromptViewModel : ObservableObject
{
    public string Heading { get; }
    public string Message { get; }
    public string Icon { get; init; } = "question";
    public string Context { get; init; } = "";
    public bool ShowMouseHint { get; }
    public bool ClapOnly { get; init; }
    public bool IsDecision { get; init; }
    public bool ShowInput { get; init; }
    private string minutes = "1", seconds = "00", error = "";
    public string InputMinutes { get => minutes; set { if (SetProperty(ref minutes, value)) Error = ""; } }
    public string InputSeconds { get => seconds; set { if (SetProperty(ref seconds, value)) Error = ""; } }
    public string Error { get => error; private set => SetProperty(ref error, value); }
    public string InputText
    {
        get => $"{InputMinutes}:{InputSeconds}";
        set { int total = DisplayFormats.ParseDuration(value); InputMinutes = (total / 60).ToString(CultureInfo.InvariantCulture); InputSeconds = (total % 60).ToString("00", CultureInfo.InvariantCulture); }
    }
    public int DurationSeconds()
    {
        if (!int.TryParse(InputMinutes, NumberStyles.None, CultureInfo.InvariantCulture, out int min) || min < 0
            || !int.TryParse(InputSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out int sec) || sec is < 0 or > 59
            || (long)min * 60 + sec is <= 0 or > int.MaxValue) throw new FormatException("Enter minutes and seconds (00–59). The timer must be longer than zero.");
        return checked(min * 60 + sec);
    }
    public int? Selected { get; private set; }
    public long? EnterTimestamp { get; private set; }
    public DateTimeOffset? EnterUtc { get; private set; }
    public TimeSpan? EnterOffset { get; private set; }
    public ImmutableArray<PromptChoice> Choices { get; private set; }
    public IEnumerable<PromptChoice> DecisionChoices => IsDecision ? Choices.Where(x => x.Description.Length > 0) : [];
    public IEnumerable<PromptChoice> FooterChoices => IsDecision ? Choices.Where(x => x.Description.Length == 0) : Choices;
    public event Action? CloseRequested;
    public PromptViewModel(string heading, string message, bool macropad, params string[] choices)
    {
        Heading = heading; Message = message; ShowMouseHint = macropad;
        Choices = choices.Select((label, i) => new PromptChoice(label, new RelayCommand(() => Select(i))) { IsPrimary = i > 0 }).ToImmutableArray();
    }
    public PromptViewModel Describe(int index, string title, string description, string icon)
    { Choices = Choices.SetItem(index, Choices[index] with { Title = title, Description = description, Icon = icon }); return this; }
    private void Select(int index)
    {
        if (ShowInput && index == 1)
        {
            try { _ = DurationSeconds(); }
            catch (FormatException ex) { Error = ex.Message; return; }
        }
        Selected = index; CloseRequested?.Invoke();
    }
    public void PhysicalEnter()
    {
        if (!ClapOnly) return;
        var clock = new PodcastHelper.Infrastructure.Time.SystemClock();
        EnterTimestamp = clock.GetTimestamp(); EnterUtc = clock.UtcNow; EnterOffset = clock.LocalNow.Offset; Select(1);
    }
}
