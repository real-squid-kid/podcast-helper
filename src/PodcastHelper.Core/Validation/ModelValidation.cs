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
            && x.Original.DescriptionMarkdown is not null && x.BudgetSeconds >= 0 && x.ActiveElapsedMs >= 0), "Invalid session segment.");
        Require(session.Segments.Select(x => x.Original.Id).Distinct().Count() == session.Segments.Length, "Session segment IDs must be unique.");
        Require(session.ReserveSeconds >= 0 && session.PlannedOverageSeconds >= 0 && session.AccountedOverrunSeconds >= 0
            && session.SessionElapsedMs >= 0 && session.ActiveElapsedMs >= 0 && session.ActiveElapsedMs <= session.SessionElapsedMs,
            "Session counters must be consistent and non-negative.");
    }
}
