using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace PodcastHelper.Core.Models;

public sealed record SessionTimer(long RemainingMs = 0, bool Running = false, long? StartedAtActiveMs = null);
public enum TimerWarning { None, Yellow, Red }
public sealed record MomentPress(EventTime Time, int OffsetSeconds, MomentCategory Category);
public sealed record Moment(Guid Id, long SessionElapsedMs, ImmutableArray<MomentPress> Presses)
{
    [JsonIgnore] public ImmutableArray<MomentCategory> Categories => Presses.Select(x => x.Category).DistinctBy(x => x.Id).ToImmutableArray();
}
