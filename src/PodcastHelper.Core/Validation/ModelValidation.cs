using PodcastHelper.Core.Models;

namespace PodcastHelper.Core.Validation;

public sealed class ModelValidationException(string message) : Exception(message);

public static class ModelValidation
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ModelValidationException(message);
    }

    public static void Settings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Require(settings.MarkerOffsetSeconds >= 0, "Marker offset must not be negative.");
        Require(settings.CameraFps > 0, "Camera FPS must be positive.");
        Require(settings.SegmentWarningMinutes > 0 && settings.SegmentWrapUpMinutes > 0,
            "Warning thresholds must be positive.");
        Require(Enum.IsDefined(settings.OverrunDistribution) && Enum.IsDefined(settings.TimerEndingSound)
            && Enum.IsDefined(settings.Animations), "Unknown setting value.");
        Require(!settings.TimerPresetSeconds.IsDefault && settings.TimerPresetSeconds.Length == 3
            && settings.TimerPresetSeconds.All(x => x > 0), "Three positive timer presets are required.");
        string[] categoryIds = ["marker", "funny", "chemistry", "clip", "review"];
        Require(!settings.Categories.IsDefault && settings.Categories.Length == 5
            && settings.Categories.All(x => x is not null && !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Icon))
            && settings.Categories.Select(x => x.Id).Order().SequenceEqual(categoryIds.Order()),
            "Five distinct built-in category IDs with names and icons are required.");
        if (settings.TimerSoundFileName is { } name)
            Require(!string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['/', '\\', ':']) < 0
                && name is not "." and not ".." && name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                && name.IndexOfAny(['<', '>', '"', '|', '?', '*', '\0']) < 0,
                "Choose an MP3 file from the sounds folder.");
        Require(settings.TimerEndingSound != TimerSound.Mp3File || settings.TimerSoundFileName is not null,
            "Choose an MP3 file from the sounds folder.");
    }

    public static void Scenario(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        Require(scenario.Id != Guid.Empty && !string.IsNullOrWhiteSpace(scenario.Name), "Scenario ID and name are required.");
        Require(scenario.TargetSeconds > 0, "Scenario duration must be positive.");
        Require(scenario.ModifiedUtc != default && scenario.ModifiedUtc.Offset == TimeSpan.Zero, "Scenario modification time must be UTC.");
        Require(!scenario.Segments.IsDefaultOrEmpty, "At least one segment is required.");
        Require(scenario.Segments.All(x => x is not null && x.Id != Guid.Empty && !string.IsNullOrWhiteSpace(x.Name)
            && x.OriginalSeconds > 0 && x.DescriptionMarkdown is not null),
            "Each segment needs an ID, name, positive duration and description.");
        Require(scenario.Segments.Select(x => x.Id).Distinct().Count() == scenario.Segments.Length, "Segment IDs must be unique.");
        // Разница суммы и цели является предупреждением, а не ошибкой сохранения.
    }

    public static void Session(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Require(session.ScenarioSnapshot is not null && session.SettingsSnapshot is not null, "Session snapshots are required.");
        Scenario(session.ScenarioSnapshot!);
        Settings(session.SettingsSnapshot!);
        Require(session.Id != Guid.Empty && Enum.IsDefined(session.State), "Invalid session identity or state.");
        Require(session.CreatedUtc != default && session.CreatedUtc.Offset == TimeSpan.Zero, "Session creation time must be UTC.");
        Require(!session.Segments.IsDefaultOrEmpty && session.CurrentSegmentIndex >= 0
            && session.CurrentSegmentIndex < session.Segments.Length, "Invalid current segment.");
        Require(session.Segments.All(x => x is not null && x.Original is not null && x.Original.Id != Guid.Empty
            && !string.IsNullOrWhiteSpace(x.Original.Name) && x.Original.OriginalSeconds > 0
            && x.Original.DescriptionMarkdown is not null && x.BudgetSeconds >= 0 && x.ActiveElapsedMs >= 0
            && x.ReserveUsedMs >= 0 && x.ReserveUsedMs <= x.ActiveElapsedMs), "Invalid session segment.");
        Require(session.Segments.Select(x => x.Original.Id).Distinct().Count() == session.Segments.Length, "Session segment IDs must be unique.");
        Require(session.ReserveSeconds >= 0 && session.PlannedOverageSeconds >= 0 && session.AccountedOverrunSeconds >= 0
            && session.ReserveSeconds <= long.MaxValue / 1000 && session.ReserveSubsecondMs is >= 0 and < 1000
            && (Int128)session.ReserveSeconds * 1000 + session.ReserveSubsecondMs <= long.MaxValue
            && session.UnfundedOverrunMs >= 0
            && session.SessionElapsedMs >= 0 && session.SessionElapsedMs <= (long.MaxValue - 9999) / TimeSpan.TicksPerMillisecond
            && session.ActiveElapsedMs >= 0 && session.ActiveElapsedMs <= session.SessionElapsedMs,
            "Session counters must be consistent and non-negative.");
        Require(session.Segments.Aggregate((Int128)0, (sum, x) => sum + x.ActiveElapsedMs) == session.ActiveElapsedMs,
            "Segment durations must add up to the active session time.");
        Require(!session.Takes.IsDefault && !session.Claps.IsDefault, "Take and clap collections are required.");
        Require(session.Takes.All(x => x is not null && x.Id != Guid.Empty && x.Started is not null), "Invalid take.");
        Require(session.Takes.Select(x => x.Id).Distinct().Count() == session.Takes.Length
            && session.Takes.Select((x, i) => x.Number == i + 1).All(x => x), "Take IDs and numbers must be unique and ordered.");
        foreach (var take in session.Takes)
        {
            Time(take.Started);
            Require(take.Started.TakeId == take.Id && take.Started.TakeElapsedMs == 0, "Invalid take start.");
            if (take.Ended is { } ended)
            {
                Time(ended);
                Require(ended.TakeId == take.Id && ended.SessionElapsedMs >= take.Started.SessionElapsedMs
                    && (Int128)ended.TakeElapsedMs.GetValueOrDefault() == (Int128)ended.SessionElapsedMs - take.Started.SessionElapsedMs + ended.TakeClockAdjustmentMs, "Invalid take end.");
            }
            Require(take.Started.SessionElapsedMs <= session.SessionElapsedMs
                && (take.Ended is null || take.Ended.SessionElapsedMs <= session.SessionElapsedMs), "Take extends beyond session time.");
            if (take.Ended is null)
                Require((Int128)session.SessionElapsedMs - take.Started.SessionElapsedMs + take.ElapsedAdjustmentMs >= 0
                    && (Int128)session.SessionElapsedMs - take.Started.SessionElapsedMs + take.ElapsedAdjustmentMs <= session.SessionElapsedMs, "Invalid adjusted take time.");
        }
        Require(session.Claps.All(x => x is not null && x.Id != Guid.Empty && x.Time is not null)
            && session.Claps.Select(x => x.Id).Distinct().Count() == session.Claps.Length, "Invalid clap IDs.");
        foreach (var clap in session.Claps)
        {
            Time(clap.Time);
            var take = session.Takes.FirstOrDefault(x => x.Id == clap.TakeId);
            Require(take is not null && clap.Time.TakeId == clap.TakeId, "Clap must belong to a take.");
            Require(clap.Time.SessionElapsedMs >= take!.Started.SessionElapsedMs
                && clap.Time.SessionElapsedMs <= session.SessionElapsedMs
                && (take.Ended is null || clap.Time.SessionElapsedMs <= take.Ended.SessionElapsedMs)
                && (Int128)clap.Time.TakeElapsedMs.GetValueOrDefault() == (Int128)clap.Time.SessionElapsedMs - take.Started.SessionElapsedMs + clap.Time.TakeClockAdjustmentMs, "Clap lies outside its take.");
        }
        int openTakes = session.Takes.Count(x => x.Ended is null);
        Require(openTakes <= 1 && (openTakes == 0 || session.Takes[^1].Ended is null), "Only the last take may be open.");
        if (session.Pause is { } pause)
        {
            Time(pause.Started);
            Require(pause.Started.SessionElapsedMs <= session.SessionElapsedMs, "Pause lies beyond session time.");
        }
        Require(session.StartedUtc is null || session.StartedUtc.Value.Offset == TimeSpan.Zero, "Session start must be UTC.");
        Require(session.FinishedUtc is null || session.FinishedUtc.Value.Offset == TimeSpan.Zero, "Session end must be UTC.");
        bool validState = session.State switch
        {
            SessionState.Preparing or SessionState.Ready => session.Takes.IsEmpty && session.Claps.IsEmpty
                && session.SessionElapsedMs == 0 && session.ActiveElapsedMs == 0 && session.StartedUtc is null
                && session.FinishedUtc is null && session.Pause is null,
            SessionState.Recording => session.StartedUtc is not null && session.FinishedUtc is null && openTakes == 1 && session.Pause is null,
            SessionState.Paused => session.StartedUtc is not null && session.FinishedUtc is null && session.Pause is not null
                && openTakes == (session.Pause.RecordingStopped == true ? 0 : 1),
            SessionState.AwaitingClap => session.StartedUtc is not null && session.FinishedUtc is null
                && session.Pause?.RecordingStopped == true && openTakes == 0,
            SessionState.Finished => session.StartedUtc is not null && session.FinishedUtc is not null && openTakes == 0 && session.Pause is null,
            _ => false
        };
        Require(validState, "Session state is inconsistent with its takes and pause.");
    }

    private static void Time(EventTime time)
    {
        Require(time.UtcTimestamp != default && time.UtcTimestamp.Offset == TimeSpan.Zero
            && time.LocalComputerTimestamp.ToUniversalTime() == time.UtcTimestamp && time.Fps > 0,
            "Invalid calendar timestamp.");
        Require(time.PredictedCameraTimecode == time.LocalComputerTimestamp.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + ":00",
            "Invalid predicted camera timecode.");
        Require(time.SegmentId != Guid.Empty && time.SessionElapsedMs >= 0 && time.ActiveElapsedMs >= 0
            && time.ActiveElapsedMs <= time.SessionElapsedMs && time.SegmentActiveElapsedMs >= 0
            && time.SegmentActiveElapsedMs <= time.ActiveElapsedMs
            && (time.TakeId is null ? time.TakeElapsedMs is null : time.TakeId != Guid.Empty && time.TakeElapsedMs >= 0
                && time.TakeElapsedMs <= time.SessionElapsedMs), "Invalid event counters.");
    }
}
