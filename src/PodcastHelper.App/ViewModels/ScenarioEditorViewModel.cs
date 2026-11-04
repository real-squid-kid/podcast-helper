using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Windows.Input;
using PodcastHelper.Core.Models;
using PodcastHelper.Core.Validation;
using PodcastHelper.Core.Services;

namespace PodcastHelper.App.ViewModels;

public sealed class SegmentDraft : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    private string name = "New segment", duration = "05:00", description = "";
    public string Name { get => name; set => SetProperty(ref name, value); }
    public string Duration { get => duration; set => SetProperty(ref duration, value); }
    public string Description { get => description; set => SetProperty(ref description, value); }
    public Segment Build() => new() { Id = Id, Name = Name.Trim(), OriginalSeconds = DisplayFormats.ParseDuration(Duration), DescriptionMarkdown = Description };
}

public sealed class ScenarioEditorViewModel : ObservableObject
{
    private readonly Guid id;
    private string name, target, message = "", error = "";
    private SegmentDraft? selected;
    public string Name { get => name; set { if (SetProperty(ref name, value)) UpdateBalance(); } }
    public string Target { get => target; set { if (SetProperty(ref target, value)) UpdateBalance(); } }
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public string Error { get => error; private set => SetProperty(ref error, value); }
    public bool ShowMouseHint { get; }
    public bool AllowStructureEdits { get; }
    public string Heading { get; }
    public string SaveHint => AllowStructureEdits ? "" : "Changes apply to this session.";
    public ObservableCollection<SegmentDraft> Segments { get; } = [];
    public SegmentDraft? Selected { get => selected; set { if (SetProperty(ref selected, value)) Notify(nameof(PreviewMarkdown)); } }
    public string PreviewMarkdown => Selected?.Description ?? "";
    public Scenario? Result { get; private set; }
    public event Action? CloseRequested;
    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand DownCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public ScenarioEditorViewModel(Scenario? source, bool macropad, bool allowStructureEdits = true, string? heading = null)
    {
        id = source?.Id ?? Guid.NewGuid(); name = source?.Name ?? "Untitled scenario";
        target = DisplayFormats.InputDuration(source?.TargetSeconds ?? 3600); ShowMouseHint = macropad;
        AllowStructureEdits = allowStructureEdits; Heading = heading ?? (allowStructureEdits ? "Scenario editor" : "Edit segment");
        foreach (var segment in source?.Segments ?? [new Segment { Name = "Segment 1", OriginalSeconds = 300 }])
            Add(new SegmentDraft { Id = segment.Id, Name = segment.Name, Duration = DisplayFormats.InputDuration(segment.OriginalSeconds), Description = segment.DescriptionMarkdown });
        Selected = Segments.FirstOrDefault();
        AddCommand = new RelayCommand(() => { if (AllowStructureEdits) { var draft = new SegmentDraft(); Add(draft); Selected = draft; UpdateBalance(); } });
        RemoveCommand = new RelayCommand(() => { if (AllowStructureEdits && Selected is { } item) { Segments.Remove(item); Selected = Segments.FirstOrDefault(); UpdateBalance(); } });
        UpCommand = new RelayCommand(() => Move(-1)); DownCommand = new RelayCommand(() => Move(1));
        SaveCommand = new RelayCommand(() =>
        {
            try
            {
                var scenario = new Scenario { Id = id, Name = Name.Trim(), TargetSeconds = DisplayFormats.ParseDuration(Target),
                    ModifiedUtc = DateTimeOffset.UtcNow, Segments = Segments.Select(x => x.Build()).ToImmutableArray() };
                ModelValidation.Scenario(scenario); Result = scenario; CloseRequested?.Invoke();
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ModelValidationException) { Error = ex is OverflowException ? "Duration is too large." : ex.Message; }
        });
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke()); UpdateBalance();
    }
    private void Add(SegmentDraft draft)
    {
        Segments.Add(draft); draft.PropertyChanged += (_, e) => { UpdateBalance(); if (e.PropertyName == nameof(SegmentDraft.Description)) Notify(nameof(PreviewMarkdown)); };
    }
    private void Move(int delta)
    {
        if (!AllowStructureEdits || Selected is null) return;
        int index = Segments.IndexOf(Selected), next = index + delta;
        if (next >= 0 && next < Segments.Count) Segments.Move(index, next);
    }
    private void UpdateBalance()
    {
        Error = "";
        try
        {
            var balance = ScenarioBudget.Analyze(DisplayFormats.ParseDuration(Target), Segments.Select(x => DisplayFormats.ParseDuration(x.Duration)));
            Message = balance.ReserveSeconds > 0 ? $"{DisplayFormats.Minutes(balance.ReserveSeconds * 1000)} left as reserve"
                : balance.PlannedOverageSeconds > 0 ? $"Over target by {DisplayFormats.Minutes(balance.PlannedOverageSeconds * 1000)} · you can still save"
                : "Timing matches the target";
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { Message = "Enter positive durations as MM:SS or seconds."; }
    }
}
