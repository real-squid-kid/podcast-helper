using System.Collections.Immutable;

namespace PodcastHelper.Core.Models;

public sealed record Segment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required int OriginalSeconds { get; init; }
    public string DescriptionMarkdown { get; init; } = string.Empty;
}

public sealed record Scenario
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required int TargetSeconds { get; init; }
    public required DateTimeOffset ModifiedUtc { get; init; }
    public ImmutableArray<Segment> Segments { get; init; } = [];
}
