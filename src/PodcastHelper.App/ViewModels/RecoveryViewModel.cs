using System.Collections.Immutable;
using System.Globalization;
using System.Windows.Input;
using PodcastHelper.Core.Abstractions;
using PodcastHelper.Core.Models;
using PodcastHelper.Core.Services;
using PodcastHelper.Core.Validation;

namespace PodcastHelper.App.ViewModels;

public sealed record SegmentChoice(int Index, string Name);
public sealed record RecoveryStatusChoice(RecoveryStatus Value, string Label);

public sealed class RecoveryViewModel : ObservableObject
{
    private readonly RecoveryPlan plan;
    private readonly IClock clock;
    private string sessionSeconds, segmentSeconds, takeSeconds, previewText = "", errorMessage = "";
    private string sessionReadable = "", segmentReadable = "", takeReadable = "";
    private string timerSeconds = "0", timerReadable = "0 min 0 s";
    private int selectedSegmentIndex;
    private RecoveryStatus selectedStatus;
    public string Heading => plan.Heading;
    public bool AtRisk => plan.EstimatedClosedMs > 180_000;
    public string SurfaceColor => AtRisk ? "#F0D3CB" : "#F1EDDF";
    public string Warning => plan.Warning ?? (plan.ClockMovedBackwards ? "The computer clock moved backwards. Review all time estimates." : "Review the recording state before continuing.");
    public string ClosedInterval => $"Closed for approximately {MinutesSeconds(plan.EstimatedClosedMs)}";
    public string TakeLabel => plan.LastKnown.Takes.IsEmpty ? "No take has started" : $"Take {plan.LastKnown.Takes[^1].Number:00}";
    public bool HasStarted => plan.LastKnown.StartedUtc is not null;
    public bool ShowMouseHint { get; }
    public ImmutableArray<SegmentChoice> Segments { get; }
    public ImmutableArray<RecoveryStatusChoice> Statuses { get; } =
    [new(RecoveryStatus.Recording, "Recording"), new(RecoveryStatus.PausedTakeContinues, "Paused · recording kept rolling"),
        new(RecoveryStatus.PausedTakeStopped, "Paused · recording stopped"), new(RecoveryStatus.AwaitingClap, "Waiting for a new clap")];
    public string SessionSeconds { get => sessionSeconds; set { if (SetProperty(ref sessionSeconds, value)) UpdatePreview(); } }
    public string SegmentSeconds { get => segmentSeconds; set { if (SetProperty(ref segmentSeconds, value)) UpdatePreview(); } }
    public string TakeSeconds { get => takeSeconds; set { if (SetProperty(ref takeSeconds, value)) UpdatePreview(); } }
    public string SessionReadable { get => sessionReadable; private set => SetProperty(ref sessionReadable, value); }
    public string SegmentReadable { get => segmentReadable; private set => SetProperty(ref segmentReadable, value); }
    public string TakeReadable { get => takeReadable; private set => SetProperty(ref takeReadable, value); }
    public string TimerSeconds { get => timerSeconds; set { if (SetProperty(ref timerSeconds, value)) UpdatePreview(); } }
    public string TimerReadable { get => timerReadable; private set => SetProperty(ref timerReadable, value); }
    public int SelectedSegmentIndex { get => selectedSegmentIndex; set { if (SetProperty(ref selectedSegmentIndex, value)) UpdatePreview(); } }
    public RecoveryStatus SelectedStatus { get => selectedStatus; set { if (SetProperty(ref selectedStatus, value)) UpdatePreview(); } }
    public string PreviewText { get => previewText; private set => SetProperty(ref previewText, value); }
    public string ErrorMessage { get => errorMessage; private set => SetProperty(ref errorMessage, value); }
    public Session? CorrectedSession { get; private set; }
    public bool StartAnew { get; private set; }
    public event Action? CloseRequested;
    public ICommand RecoverCommand { get; }
    public ICommand StartAnewCommand { get; }

    public RecoveryViewModel(RecoveryPlan plan, IClock clock, bool hasMacropad)
    {
        this.plan = plan; this.clock = clock; ShowMouseHint = hasMacropad;
        var s = plan.LastKnown;
        Segments = s.Segments.Select((x, i) => new SegmentChoice(i, x.Original.Name)).ToImmutableArray();
        selectedSegmentIndex = s.CurrentSegmentIndex;
        sessionSeconds = Seconds(s.StartedUtc is null ? 0 : checked(s.SessionElapsedMs + plan.EstimatedClosedMs));
        segmentSeconds = Seconds(s.Segments[s.CurrentSegmentIndex].ActiveElapsedMs);
        timerSeconds = Seconds(s.Timer.RemainingMs);
        var take = s.Takes.LastOrDefault();
        takeSeconds = Seconds(take is null ? 0 : take.Ended?.TakeElapsedMs ?? s.SessionElapsedMs - take.Started.SessionElapsedMs + take.ElapsedAdjustmentMs);
        selectedStatus = s.State == SessionState.AwaitingClap ? RecoveryStatus.AwaitingClap
            : s.Pause?.RecordingStopped == true ? RecoveryStatus.PausedTakeStopped : RecoveryStatus.PausedTakeContinues;
        RecoverCommand = new RelayCommand(() =>
        {
            try { CorrectedSession = Build(); ErrorMessage = ""; CloseRequested?.Invoke(); }
            catch (Exception ex) when (ex is ModelValidationException or OverflowException or FormatException)
            { ErrorMessage = ex is ModelValidationException ? ex.Message : "Enter valid non-negative times in seconds (up to three decimal places)."; }
        });
        StartAnewCommand = new RelayCommand(() => { StartAnew = true; CloseRequested?.Invoke(); });
        UpdatePreview();
    }

    private Session Build() => RecoveryEditor.Apply(plan,
        new(Parse(SessionSeconds), SelectedSegmentIndex, Parse(SegmentSeconds), Parse(TakeSeconds), SelectedStatus) { TimerRemainingMs = Parse(TimerSeconds) }, clock.UtcNow, clock.LocalNow.Offset);
    private void UpdatePreview()
    {
        SessionReadable = Readable(SessionSeconds);
        SegmentReadable = Readable(SegmentSeconds);
        TakeReadable = Readable(TakeSeconds);
        TimerReadable = Readable(TimerSeconds);
        try
        {
            var corrected = Build();
            long reserveMs = corrected.ReserveSeconds * 1000 + corrected.ReserveSubsecondMs;
            PreviewText = $"Reserve left: {MinutesSeconds(reserveMs)}" + (corrected.UnfundedOverrunMs > 0 ? $" · Overdue: {MinutesSeconds(corrected.UnfundedOverrunMs)}" : "");
        }
        catch (Exception ex) when (ex is ModelValidationException or OverflowException or FormatException)
        { PreviewText = "Review the time fields. Changes have not been applied."; }
    }
    private static string Seconds(long ms) => (ms / 1000m).ToString("0.###", CultureInfo.InvariantCulture);
    private static string MinutesSeconds(long ms) => $"{(ms / 60_000).ToString(CultureInfo.InvariantCulture)} min {(ms % 60_000 / 1000m).ToString("0.###", CultureInfo.InvariantCulture)} s";
    private static string Readable(string text)
    {
        try { return MinutesSeconds(Parse(text)); }
        catch (Exception ex) when (ex is FormatException or OverflowException) { return "—"; }
    }
    private static long Parse(string text)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value)
            || value < 0 || decimal.Truncate(value * 1000) != value * 1000)
            throw new FormatException();
        return checked((long)(value * 1000));
    }
}
