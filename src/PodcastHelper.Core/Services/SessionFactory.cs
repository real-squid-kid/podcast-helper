using System.Collections.Immutable;
using PodcastHelper.Core.Abstractions;
using PodcastHelper.Core.Models;
using PodcastHelper.Core.Validation;

namespace PodcastHelper.Core.Services;

public sealed class SessionFactory(IClock clock)
{
    public Session Create(Scenario scenario, AppSettings settings)
    {
        ModelValidation.Scenario(scenario);
        ModelValidation.Settings(settings);
        var balance = ScenarioBudget.Analyze(scenario.TargetSeconds, scenario.Segments.Select(x => x.OriginalSeconds));
        // Все вложенные данные неизменяемы; изменения выпуска не изменяют шаблон.
        return new Session
        {
            ScenarioSnapshot = scenario with { },
            SettingsSnapshot = settings with { },
            CreatedUtc = clock.UtcNow,
            Segments = scenario.Segments.Select(x => new SessionSegment(x with { }, x.OriginalSeconds, 0)).ToImmutableArray(),
            ReserveSeconds = balance.ReserveSeconds,
            PlannedOverageSeconds = balance.PlannedOverageSeconds
        };
    }
}
