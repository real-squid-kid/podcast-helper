using PodcastHelper.Core.Models;
using PodcastHelper.Core.Validation;

namespace PodcastHelper.Infrastructure.Storage;

public sealed class PortableDataStore(PortablePaths paths, JsonDocumentStore documents)
{
    public async Task<AppSettings> InitializeSettingsAsync(CancellationToken ct = default)
    {
        var settings = await LoadSettingsAsync(ct).ConfigureAwait(false);
        await documents.CheckWriteAccessAsync(Path.GetDirectoryName(paths.SettingsFile)!, ct).ConfigureAwait(false);
        if (settings is not null) return settings;
        settings = new AppSettings();
        await SaveSettingsAsync(settings, ct).ConfigureAwait(false);
        return settings;
    }

    public Task<AppSettings?> LoadSettingsAsync(CancellationToken ct = default) =>
        documents.LoadAsync<AppSettings>(paths.SettingsFile, "settings", ModelValidation.Settings, ct);
    public Task SaveSettingsAsync(AppSettings value, CancellationToken ct = default) =>
        documents.SaveAsync(paths.SettingsFile, "settings", value, ModelValidation.Settings, ct);

    public Task<Scenario?> LoadScenarioAsync(Guid id, CancellationToken ct = default) =>
        LoadIdentifiedAsync<Scenario>(paths.ScenarioFile(id), "scenario", id, x => x.Id, ModelValidation.Scenario, ct);
    public Task SaveScenarioAsync(Scenario value, CancellationToken ct = default)
    {
        ModelValidation.Scenario(value);
        return documents.SaveAsync(paths.ScenarioFile(value.Id), "scenario", value, x => ValidateIdentity(x, value.Id, y => y.Id, ModelValidation.Scenario), ct);
    }

    public Task<Session?> LoadSessionAsync(Guid id, CancellationToken ct = default) =>
        LoadIdentifiedAsync<Session>(paths.SessionFile(id), "session", id, x => x.Id, ModelValidation.Session, ct);
    public Task SaveSessionAsync(Session value, CancellationToken ct = default)
    {
        ModelValidation.Session(value);
        return documents.SaveAsync(paths.SessionFile(value.Id), "session", value, x => ValidateIdentity(x, value.Id, y => y.Id, ModelValidation.Session), ct);
    }

    private async Task<T?> LoadIdentifiedAsync<T>(string path, string kind, Guid id, Func<T, Guid> getId, Action<T> validate, CancellationToken ct) where T : class
    {
        var value = await documents.LoadAsync(path, kind, validate, ct).ConfigureAwait(false);
        if (value is not null && getId(value) != id) throw new InvalidDocumentException("The file ID does not match its name.");
        return value;
    }

    private static void ValidateIdentity<T>(T value, Guid id, Func<T, Guid> getId, Action<T> validate)
    {
        validate(value);
        if (getId(value) != id) throw new InvalidDocumentException("The file ID does not match its name.");
    }
}
