using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using PodcastHelper.Core.Models;
using PodcastHelper.Core.Validation;

namespace PodcastHelper.App.ViewModels;

public sealed class CategoryDraft
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Icon { get; set; }
}
public sealed record StrategyChoice(OverrunDistribution Value, string Label);
public sealed record SoundChoice(TimerSound Value, string Label);
public sealed record AnimationChoice(AnimationPreference Value, string Label);
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings source;
    private string error = "", soundFileName;
    public bool ShowMouseHint => source.HasMacropad || HasMacropad;
    private bool hasMacropad;
    public bool HasMacropad { get => hasMacropad; set { if (SetProperty(ref hasMacropad, value)) Notify(nameof(ShowMouseHint)); } }
    public string MarkerOffset { get; set; }
    public string Fps { get; set; }
    public string YellowMinutes { get; set; }
    public string RedMinutes { get; set; }
    public string Preset1 { get; set; }
    public string Preset2 { get; set; }
    public string Preset3 { get; set; }
    public OverrunDistribution Distribution { get; set; }
    private TimerSound sound;
    public TimerSound Sound { get => sound; set { if (SetProperty(ref sound, value)) Notify(nameof(UsesMp3)); } }
    public bool UsesMp3 => Sound == TimerSound.Mp3File;
    public AnimationPreference Animations { get; set; }
    public string SoundFileName { get => soundFileName; set => SetProperty(ref soundFileName, value); }
    public ObservableCollection<string> AvailableSounds { get; } = [];
    public string Error { get => error; private set => SetProperty(ref error, value); }
    public ObservableCollection<CategoryDraft> Categories { get; } = [];
    public ImmutableArray<StrategyChoice> Strategies { get; } = [new(OverrunDistribution.AdjustNextSegment, "Adjust next segment"), new(OverrunDistribution.AdjustRemainingProportionally, "Adjust remaining proportionally")];
    public ImmutableArray<SoundChoice> Sounds { get; } = [new(TimerSound.Windows, "Windows"), new(TimerSound.Mp3File, "MP3 file"), new(TimerSound.Off, "Off")];
    public ImmutableArray<AnimationChoice> AnimationChoices { get; } = [new(AnimationPreference.System, "Follow Windows"), new(AnimationPreference.Reduced, "Reduced"), new(AnimationPreference.Full, "Full")];
    public AppSettings? Result { get; private set; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public event Action? CloseRequested;
    public const string TimecodeDisclaimer = "SMPTE timecode is predicted based on computer time. FF is always :00.";
    public string Disclaimer => TimecodeDisclaimer;

    public SettingsViewModel(AppSettings settings)
    {
        source = settings; HasMacropad = settings.HasMacropad; MarkerOffset = settings.MarkerOffsetSeconds.ToString(CultureInfo.InvariantCulture);
        Fps = settings.CameraFps.ToString(CultureInfo.InvariantCulture); YellowMinutes = settings.SegmentWarningMinutes.ToString(CultureInfo.InvariantCulture);
        RedMinutes = settings.SegmentWrapUpMinutes.ToString(CultureInfo.InvariantCulture);
        Preset1 = DisplayFormats.InputDuration(settings.TimerPresetSeconds[0]); Preset2 = DisplayFormats.InputDuration(settings.TimerPresetSeconds[1]); Preset3 = DisplayFormats.InputDuration(settings.TimerPresetSeconds[2]);
        Distribution = settings.OverrunDistribution; Sound = settings.TimerEndingSound; Animations = settings.Animations; soundFileName = settings.TimerSoundFileName ?? "";
        foreach (var category in settings.Categories) Categories.Add(new() { Id = category.Id, Name = category.Name, Icon = DisplayFormats.Icon(category.Icon) });
        SaveCommand = new RelayCommand(() =>
        {
            try
            {
                var result = source with { HasMacropad = HasMacropad, MarkerOffsetSeconds = int.Parse(MarkerOffset, CultureInfo.InvariantCulture),
                    CameraFps = decimal.Parse(Fps, CultureInfo.InvariantCulture), SegmentWarningMinutes = int.Parse(YellowMinutes, CultureInfo.InvariantCulture),
                    SegmentWrapUpMinutes = int.Parse(RedMinutes, CultureInfo.InvariantCulture), OverrunDistribution = Distribution,
                    TimerPresetSeconds = [DisplayFormats.ParseDuration(Preset1), DisplayFormats.ParseDuration(Preset2), DisplayFormats.ParseDuration(Preset3)],
                    TimerEndingSound = Sound, TimerSoundFileName = string.IsNullOrWhiteSpace(SoundFileName) ? null : SoundFileName,
                    Animations = Animations, Categories = Categories.Select(x => new MomentCategory(x.Id, x.Name.Trim(), x.Icon.Trim())).ToImmutableArray() };
                ModelValidation.Settings(result); Result = result; CloseRequested?.Invoke();
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ModelValidationException)
            { Error = ex is ModelValidationException ? ex.Message : "Review numeric values and timer durations (MM:SS)."; }
        });
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }
}
