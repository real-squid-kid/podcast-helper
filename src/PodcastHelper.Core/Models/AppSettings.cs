using System.Collections.Immutable;

namespace PodcastHelper.Core.Models;

public enum OverrunDistribution { AdjustNextSegment, AdjustRemainingProportionally }
public enum TimerSound { Windows, Mp3File, Off }
public enum AnimationPreference { System, Reduced, Full }

public sealed record MomentCategory(string Id, string Name, string Icon);

public sealed record AppSettings
{
    public bool HasMacropad { get; init; }
    public int MarkerOffsetSeconds { get; init; } = 5;
    public decimal CameraFps { get; init; } = 25;
    public int SegmentWarningMinutes { get; init; } = 5;
    public int SegmentWrapUpMinutes { get; init; } = 1;
    public OverrunDistribution OverrunDistribution { get; init; } = OverrunDistribution.AdjustNextSegment;
    public ImmutableArray<int> TimerPresetSeconds { get; init; } = [60, 120, 180];
    public TimerSound TimerEndingSound { get; init; } = TimerSound.Windows;
    public string? TimerSoundFileName { get; init; }
    public AnimationPreference Animations { get; init; } = AnimationPreference.System;
    // Значки — собственные семантические ID, графика будет добавлена на этапе UI.
    public ImmutableArray<MomentCategory> Categories { get; init; } =
    [
        new("marker", "Marker", "marker"),
        new("funny", "Funny", "funny"),
        new("chemistry", "Chemistry", "chemistry"),
        new("clip", "Clip", "clip"),
        new("review", "Review", "review")
    ];
}
