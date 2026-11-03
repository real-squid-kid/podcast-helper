namespace PodcastHelper.Infrastructure.Storage;

public sealed class PortablePaths
{
    public string BaseDirectory { get; }

    public PortablePaths(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        if (!Path.IsPathFullyQualified(baseDirectory))
            throw new ArgumentException("Application directory must be absolute.", nameof(baseDirectory));
        BaseDirectory = Path.GetFullPath(baseDirectory);
    }

    public string SettingsFile => Path.Combine(BaseDirectory, "config", "settings.json");
    public string ScenarioFile(Guid id) => Path.Combine(BaseDirectory, "scenarios", FileName(id));
    public string SessionFile(Guid id)
    {
        _ = FileName(id);
        return Path.Combine(BaseDirectory, "sessions", id.ToString("N"), "session.json");
    }
    public string SoundsDirectory => Path.Combine(BaseDirectory, "sounds");
    public string ExportsDirectory => Path.Combine(BaseDirectory, "exports");
    public string LogsDirectory => Path.Combine(BaseDirectory, "logs");
    public string LibrariesDirectory => Path.Combine(BaseDirectory, "libs");
    public string LicensesDirectory => Path.Combine(BaseDirectory, "licenses");

    private static string FileName(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A non-empty ID is required.", nameof(id));
        return $"{id:N}.json";
    }
}
