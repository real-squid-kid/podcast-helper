using System.Globalization;

namespace PodcastHelper.App.ViewModels;

public static class DisplayFormats
{
    public static string Time(long ms) { long s = Math.Max(0, ms) / 1000; return $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}"; }
    public static string Minutes(long ms) { long s = Math.Max(0, ms + 999) / 1000; return $"{s / 60:00}:{s % 60:00}"; }
    public static string InputDuration(int seconds) => $"{seconds / 60:00}:{seconds % 60:00}";
    public static int ParseDuration(string text)
    {
        string[] pieces = text.Trim().Split(':');
        if (pieces.Length == 1 && int.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) && seconds > 0) return seconds;
        if (pieces.Length == 2 && int.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) && minutes >= 0
            && int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out int remainder) && remainder is >= 0 and < 60)
        { int total = checked(minutes * 60 + remainder); if (total > 0) return total; }
        throw new FormatException("Enter a positive duration as MM:SS or seconds.");
    }
    public static string Icon(string value) => value switch { "marker" => "◇", "funny" => "✦", "chemistry" => "↔", "clip" => "▣", "review" => "◎", _ => value };
}
