using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PodcastHelper.App;

public static class ApplicationLocation
{
    private static readonly Assembly Assembly = typeof(App).Assembly;
    private static string? Metadata(string name) => Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == name)?.Value;
    public static bool IsPortable => Metadata("PortableLayout") == "libs";
    public static Uri RepositoryUri { get; } = new("https://github.com/real-squid-kid/podcast-helper");
    public static string RootDirectory => IsPortable
        ? Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName : AppContext.BaseDirectory;
    public static string Version => (Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0").Split('+')[0];
    public static string Build
    {
        get
        {
            string? commit = Metadata("BuildCommit") ?? Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').ElementAtOrDefault(1);
            return (string.IsNullOrWhiteSpace(commit) ? "local build" : commit[..Math.Min(12, commit.Length)]) + (Metadata("BuildModified") == "true" ? " · modified" : "");
        }
    }
    public static string DiagnosticInfo() => string.Join(Environment.NewLine,
        "PodcastHelper " + Version, "Build: " + Build, "Windows: " + RuntimeInformation.OSDescription,
        "Runtime: " + RuntimeInformation.FrameworkDescription, "Architecture: " + RuntimeInformation.ProcessArchitecture,
        "OS architecture: " + RuntimeInformation.OSArchitecture, "Platform: Windows",
        "Operation: " + (IsPortable ? "Portable self-contained, offline" : "Development, offline"),
        "Data location: Application folder", "Working directory: " + (Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(RootDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ? "Application folder" : "Other folder"));
}
