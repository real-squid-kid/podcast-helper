using PodcastHelper.Core.Models;

namespace PodcastHelper.App.ViewModels;

public sealed class SegmentRow : ObservableObject
{
    public Guid Id { get; }
    public Segment Segment { get; private set; }
    private string label = "", duration = "", state = "";
    public string Label { get => label; private set => SetProperty(ref label, value); }
    public string Duration { get => duration; private set => SetProperty(ref duration, value); }
    public string State { get => state; private set => SetProperty(ref state, value); }
    public int Index { get; private set; }
    public SegmentRow(SessionSegment source, int index, int current, bool finished) { Id = source.Original.Id; Segment = source.Original; Update(source, index, current, finished); }
    public void Update(SessionSegment source, int index, int current, bool finished)
    {
        Segment = source.Original; Index = index; Label = source.Original.Name;
        Duration = DisplayFormats.InputDuration(source.BudgetSeconds);
        State = index < current || finished && index == current ? "DONE" : index == current ? "CURRENT" : "NEXT";
    }
}
public sealed record TimelineRow(Guid? MomentId, string Kind, string Time, string Glyph, string Label, string Detail);
public sealed record MomentButton(string Id, string Label, string Icon, System.Windows.Input.ICommand Command);
