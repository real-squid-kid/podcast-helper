namespace PodcastHelper.Core.Models;

// Сохраняются только доли тиков, а не process-specific монотонный timestamp.
public sealed record EngineCheckpoint(Session Session, int SessionFractionTicks = 0, int ActiveFractionTicks = 0, bool ClapDialogOpen = false);
