using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using PodcastHelper.Core.Models;
using PodcastHelper.Infrastructure.Storage;

namespace PodcastHelper.App;

public sealed class TimerSoundPlayer(PortablePaths paths, Action<string> report)
{
    private MediaPlayer? player;
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MessageBeep(uint type);
    public void Play(AppSettings settings)
    {
        if (settings.TimerEndingSound == TimerSound.Off) return;
        if (settings.TimerEndingSound == TimerSound.Windows) { MessageBeep(0x40); return; }
        try
        {
            string? name = settings.TimerSoundFileName;
            if (name is null || Path.GetFileName(name) != name) throw new IOException();
            string path = Path.Combine(paths.SoundsDirectory, name);
            if (!File.Exists(path)) throw new FileNotFoundException();
            player?.Close(); player = new MediaPlayer();
            player.MediaFailed += async (_, _) => await FailedAsync();
            player.MediaEnded += (_, _) => player?.Close();
            player.Open(new Uri(path)); player.Play();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { _ = FailedAsync(); }
    }
    private async Task FailedAsync()
    {
        report("Timer sound is unavailable. Timer events were saved.");
        try { Directory.CreateDirectory(paths.LogsDirectory); await File.AppendAllTextAsync(Path.Combine(paths.LogsDirectory, "sound.log"), $"{DateTimeOffset.UtcNow:O} Timer ending sound unavailable.\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Close() => player?.Close();
}
