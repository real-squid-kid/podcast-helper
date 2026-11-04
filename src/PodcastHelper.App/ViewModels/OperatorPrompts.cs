using PodcastHelper.Core.Models;

namespace PodcastHelper.App.ViewModels;

// Тексты и представление решений оператора. Никаких правил времени или хранения.
public static class OperatorPrompts
{
    public static PromptViewModel Pause(bool mouse, bool correction = false, bool? previous = null)
    {
        var vm = new PromptViewModel("Was recording stopped?", correction
            ? "When you paused, did you stop the cameras or audio recorders?"
            : "Did you stop the cameras or audio recorders when you paused?", mouse, correction ? ["Cancel", "Yes", "No"] : ["Yes", "No"])
        { Icon = "pause", IsDecision = true, Context = correction ? previous == true ? "Current choice: recording was stopped" : "Current choice: recording kept rolling" : "" };
        int first = correction ? 1 : 0;
        return vm.Describe(first, "Yes, I stopped recording", "Resume will start a new take after a clap.", "stop")
            .Describe(first + 1, "No, it kept rolling", "Resume will continue this take. No new clap needed.", "play");
    }
    public static PromptViewModel Clap(bool mouse, SessionState state, int take) => new("Command the clap then press Enter as precisely as possible when the clap occurs",
        state == SessionState.Recording ? $"Add a sync mark to Take {take:00}. The clocks keep running."
        : state == SessionState.Ready ? "Start the session and Take 01."
        : $"Start Take {take + 1:00} and resume the segment.", mouse, "Cancel") { Icon = "clap", ClapOnly = true };
    public static PromptViewModel Timer(bool mouse, bool replacing) => new("Set timer", replacing ? "Start a new countdown in place of the current timer." : "How long should the countdown run?", mouse, "Cancel", "Start timer")
        { Icon = "clock", ShowInput = true, InputText = "01:00" };
    public static PromptViewModel Finish(bool mouse, int take) => new("Finish this session?", $"Close Take {take:00} and save the session. The timer will stop.", mouse, "Keep session open", "Finish session") { Icon = "stop" };
    public static PromptViewModel Remove(bool mouse, string name, string consequence, string action) => new($"Remove “{name}”?", consequence, mouse, "Keep segment", action) { Icon = "trash" };
    public static PromptViewModel SaveScenario(bool mouse, string name) => new("Update the scenario?", $"Save the reviewed changes to “{name}” for future sessions?", mouse, "Cancel", "Save Changes to Scenario") { Icon = "save" };
}
