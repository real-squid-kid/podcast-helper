using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PodcastHelper.Core.Models;
using PodcastHelper.Infrastructure.Sessions;
using PodcastHelper.Infrastructure.Storage;

namespace PodcastHelper.Infrastructure.Export;

public sealed record ExportTable(string Name, ImmutableArray<string> Columns, ImmutableArray<ImmutableArray<string>> Rows);
public sealed record ExportDocument(ImmutableArray<ExportTable> Tables);
public enum SessionExportFormat { Csv, Xls }
public enum SessionExportContent { Full, Moments }
public sealed class ExportLimitException(string message) : Exception(message);

public static class SessionExportProjection
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    public static string Time(long ms) => $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}";
    private static string Number(long value) => value.ToString(Culture);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    public static ExportDocument Create(SessionArchive archive, SessionExportContent content = SessionExportContent.Full)
    {
        if (!Enum.IsDefined(content)) throw new ArgumentOutOfRangeException(nameof(content));
        if (content == SessionExportContent.Moments) return CreateMoments(archive);
        var session = archive.Session;
        Segment? InitialSegment(Guid id) => session.ScenarioSnapshot.Segments.FirstOrDefault(x => x.Id == id)
            ?? archive.Journal.Records.FirstOrDefault(x => x.Command.Kind == SessionCommandKind.AddSegment && x.Command.Segment?.Id == id)?.Command.Segment;
        var tables = ImmutableArray.CreateBuilder<ExportTable>();
        tables.Add(new("Session", ["field", "value"], new Dictionary<string, string>
        {
            ["schemaVersion"] = "1", ["sessionId"] = session.Id.ToString(), ["scenarioId"] = session.ScenarioSnapshot.Id.ToString(),
            ["scenarioName"] = session.ScenarioSnapshot.Name, ["status"] = archive.Status,
            ["createdUtc"] = session.CreatedUtc.ToString("O"), ["startedUtc"] = session.StartedUtc?.ToString("O") ?? "", ["finishedUtc"] = session.FinishedUtc?.ToString("O") ?? "",
            ["lastSavedUtc"] = archive.Journal.Records[^1].UtcTimestamp.ToString("O"), ["plannedSeconds"] = Number(session.ScenarioSnapshot.TargetSeconds),
            ["sessionElapsedMs"] = Number(session.SessionElapsedMs), ["activeElapsedMs"] = Number(session.ActiveElapsedMs),
            ["fps"] = session.SettingsSnapshot.CameraFps.ToString(Culture), ["markerOffsetSeconds"] = Number(session.SettingsSnapshot.MarkerOffsetSeconds),
            ["categories"] = Json(session.SettingsSnapshot.Categories), ["timerPresetSeconds"] = Json(session.SettingsSnapshot.TimerPresetSeconds),
            ["reserveMs"] = Number(session.ReserveSeconds * 1000 + session.ReserveSubsecondMs), ["plannedOverageSeconds"] = Number(session.PlannedOverageSeconds),
            ["archiveReason"] = archive.Journal.State.ArchiveReason ?? "", ["incompleteJournalTail"] = archive.Journal.IncompleteTail.ToString(),
            ["timecodeNote"] = "SMPTE timecode is predicted based on computer time. FF is always :00."
        }.Select(x => ImmutableArray.Create(x.Key, x.Value)).ToImmutableArray()));

        ImmutableArray<string> columns = ["id", "sessionTime", "takeTime", "takeId", "utcTimestamp", "computerTime", "utcOffset", "predictedCameraTimecode", "fps",
            "eventType", "categories", "categoryIds", "segmentId", "segmentName", "details", "sessionElapsedMs", "activeElapsedMs", "segmentActiveElapsedMs",
            "pressedUtc", "pressedComputerTime", "pressedSessionElapsedMs", "markerOffsetSeconds", "momentId", "takeClockAdjustmentMs"];
        ImmutableArray<string> Row(Guid id, EventTime time, string type, string details = "", string categories = "", string categoryIds = "",
            EventTime? press = null, int? offset = null, Guid? momentId = null) =>
        [id.ToString(), Time(time.SessionElapsedMs), time.TakeElapsedMs is { } take ? Time(take) : "", time.TakeId?.ToString() ?? "", time.UtcTimestamp.ToString("O"),
            time.LocalComputerTimestamp.ToString("O"), time.LocalComputerTimestamp.ToString("zzz"), time.PredictedCameraTimecode, time.Fps.ToString(Culture), type, categories,
            categoryIds, time.SegmentId == Guid.Empty ? "" : time.SegmentId.ToString(), session.Segments.FirstOrDefault(x => x.Original.Id == time.SegmentId)?.Original.Name ?? "",
            details, Number(time.SessionElapsedMs), Number(time.ActiveElapsedMs), Number(time.SegmentActiveElapsedMs), press?.UtcTimestamp.ToString("O") ?? "",
            press?.LocalComputerTimestamp.ToString("O") ?? "", press is null ? "" : Number(press.SessionElapsedMs), offset?.ToString(Culture) ?? "", momentId?.ToString() ?? "", Number(time.TakeClockAdjustmentMs)];

        tables.Add(new("Events", columns, archive.Journal.Records.SelectMany(x => x.Events)
            .Select(x => Row(x.Id, x.Time, x.Type.ToString(), x.Detail ?? "")).ToImmutableArray()));
        var moments = ImmutableArray.CreateBuilder<ImmutableArray<string>>(); var presses = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        foreach (var moment in session.Moments)
        {
            var first = moment.Presses[0];
            // Якорь хранится в модели. Абсолютные часы и привязки сохраняем как исходные данные
            // нажатий, не изобретая UTC/активное время через паузу, Undo или перевод часов ОС.
            moments.Add(ImmutableArray.Create(moment.Id.ToString(), Time(moment.SessionElapsedMs), Number(moment.SessionElapsedMs),
                string.Join(", ", moment.Categories.Select(x => x.Name)), Json(moment.Categories.Select(x => x.Id)),
                first.Time.UtcTimestamp.ToString("O"), first.Time.LocalComputerTimestamp.ToString("O"), Number(first.Time.SessionElapsedMs),
                Number(first.OffsetSeconds), moment.Id.ToString(), Json(moment.Presses)));
            foreach (var press in moment.Presses)
                presses.Add(Row(moment.Id, press.Time, "Moment press", Json(press.Category), press.Category.Name, press.Category.Id, press.Time, press.OffsetSeconds, moment.Id));
        }
        tables.Add(new("Moments", ["id", "sessionTime", "sessionElapsedMs", "categories", "categoryIds", "pressedUtc", "pressedComputerTime", "pressedSessionElapsedMs", "markerOffsetSeconds", "momentId", "presses"], moments.ToImmutable())); tables.Add(new("Moment presses", columns, presses.ToImmutable()));
        tables.Add(new("Segments", ["segmentId", "order", "name", "originalSeconds", "sessionPlannedSeconds", "budgetSeconds", "actualMs", "reserveUsedMs", "removed", "description", "templateName", "templateDescription"],
            session.Segments.Select((x, i) => ImmutableArray.Create(x.Original.Id.ToString(), Number(i + 1), x.Original.Name,
                InitialSegment(x.Original.Id)?.OriginalSeconds.ToString(Culture) ?? "", Number(x.Original.OriginalSeconds),
                Number(x.BudgetSeconds), Number(x.ActiveElapsedMs), Number(x.ReserveUsedMs), x.RemovedFromScenario.ToString(), x.Original.DescriptionMarkdown,
                session.ScenarioSnapshot.Segments.FirstOrDefault(s => s.Id == x.Original.Id)?.Name ?? "", session.ScenarioSnapshot.Segments.FirstOrDefault(s => s.Id == x.Original.Id)?.DescriptionMarkdown ?? "")).ToImmutableArray()));
        var removed = session.ScenarioSnapshot.Segments.Concat(archive.Journal.Records.Where(x => x.Command.Segment is not null).Select(x => x.Command.Segment!))
            .GroupBy(x => x.Id).Select(x => x.Last()).Where(x => session.Segments.All(s => s.Original.Id != x.Id));
        var segmentTable = tables[^1];
        tables[^1] = segmentTable with { Rows = segmentTable.Rows.AddRange(removed.Select(x => ImmutableArray.Create(x.Id.ToString(), "", x.Name,
            Number(InitialSegment(x.Id)?.OriginalSeconds ?? x.OriginalSeconds), Number(x.OriginalSeconds), "0", "0", "0", "True", x.DescriptionMarkdown,
            session.ScenarioSnapshot.Segments.FirstOrDefault(s => s.Id == x.Id)?.Name ?? "", session.ScenarioSnapshot.Segments.FirstOrDefault(s => s.Id == x.Id)?.DescriptionMarkdown ?? ""))) };
        tables.Add(new("Takes", ["takeId", "number", "startedUtc", "startedComputerTime", "startedSessionMs", "endedUtc", "endedComputerTime", "endedSessionMs", "elapsedAdjustmentMs", "startDetails", "endDetails"],
            session.Takes.Select(x => ImmutableArray.Create(x.Id.ToString(), Number(x.Number), x.Started.UtcTimestamp.ToString("O"), x.Started.LocalComputerTimestamp.ToString("O"),
                Number(x.Started.SessionElapsedMs), x.Ended?.UtcTimestamp.ToString("O") ?? "", x.Ended?.LocalComputerTimestamp.ToString("O") ?? "",
                x.Ended is null ? "" : Number(x.Ended.SessionElapsedMs), Number(x.ElapsedAdjustmentMs), Json(x.Started), Json(x.Ended))).ToImmutableArray()));
        tables.Add(new("Commands", ["id", "sequence", "utcTimestamp", "command", "undoOf", "parameters"], archive.Journal.Records
            .Where(x => x.Command.Kind != SessionCommandKind.Checkpoint)
            .Select(x => ImmutableArray.Create(x.Id.ToString(), Number(x.Sequence), x.UtcTimestamp.ToString("O"), x.Command.Kind.ToString(), x.UndoOf?.ToString() ?? "", Json(x.Command))).ToImmutableArray()));
        return new(tables.ToImmutable());
    }

    private static ExportDocument CreateMoments(SessionArchive archive)
    {
        var session = archive.Session;
        string TakeNumber(Guid? id) => session.Takes.FirstOrDefault(x => x.Id == id)?.Number.ToString(Culture) ?? "";
        return new([
            new("Session", ["field", "value"], new Dictionary<string, string>
            {
                ["scenarioName"] = session.ScenarioSnapshot.Name, ["sessionId"] = session.Id.ToString(),
                ["startedUtc"] = session.StartedUtc?.ToString("O") ?? "", ["finishedUtc"] = session.FinishedUtc?.ToString("O") ?? "",
                ["sessionTime"] = Time(session.SessionElapsedMs), ["status"] = archive.Status,
                ["fps"] = session.SettingsSnapshot.CameraFps.ToString(Culture),
                ["timecodeNote"] = "SMPTE timecode is predicted based on computer time. FF is always :00."
            }.Select(x => ImmutableArray.Create(x.Key, x.Value)).ToImmutableArray()),
            new("Moments", ["sessionTime", "categories", "takeNumber", "pressedTakeTime", "pressedSessionTime", "markerOffsetSeconds", "pressedUtc", "pressedComputerTime", "segmentName", "momentId"],
                session.Moments.Select(x =>
                {
                    var first = x.Presses[0]; var time = first.Time;
                    return ImmutableArray.Create(Time(x.SessionElapsedMs), string.Join(", ", x.Categories.Select(c => c.Name)), TakeNumber(time.TakeId),
                        time.TakeElapsedMs is { } take ? Time(take) : "", Time(time.SessionElapsedMs), Number(first.OffsetSeconds), time.UtcTimestamp.ToString("O"),
                        time.LocalComputerTimestamp.ToString("O"), session.Segments.FirstOrDefault(s => s.Original.Id == time.SegmentId)?.Original.Name ?? "", x.Id.ToString());
                }).ToImmutableArray()),
            new("Claps", ["sessionTime", "takeTime", "takeNumber", "utcTimestamp", "computerTime", "predictedCameraTimecode", "fps"],
                archive.Journal.Records.SelectMany(x => x.Events).Where(x => x.Type == SessionEventType.Clap).Select(x => ImmutableArray.Create(
                    Time(x.Time.SessionElapsedMs), x.Time.TakeElapsedMs is { } take ? Time(take) : "", TakeNumber(x.Time.TakeId), x.Time.UtcTimestamp.ToString("O"),
                    x.Time.LocalComputerTimestamp.ToString("O"), x.Time.PredictedCameraTimecode, x.Time.Fps.ToString(Culture))).ToImmutableArray()),
            new("Takes", ["takeNumber", "startedUtc", "endedUtc", "sessionStart", "sessionEnd"], session.Takes.Select(x => ImmutableArray.Create(
                Number(x.Number), x.Started.UtcTimestamp.ToString("O"), x.Ended?.UtcTimestamp.ToString("O") ?? "", Time(x.Started.SessionElapsedMs),
                x.Ended is null ? "" : Time(x.Ended.SessionElapsedMs))).ToImmutableArray())]);
    }
}

public static class SessionCsvWriter
{
    public static byte[] Write(ExportDocument document, CancellationToken ct = default)
    {
        var columns = new[] { "table" }.Concat(document.Tables.SelectMany(x => x.Columns).Distinct(StringComparer.Ordinal)).Append("literalValues").ToArray();
        var text = new StringBuilder(); Line(columns);
        foreach (var table in document.Tables)
        foreach (var row in table.Rows)
        {
            ct.ThrowIfCancellationRequested(); var fields = table.Columns.Zip(row).ToDictionary(x => x.First, x => x.Second, StringComparer.Ordinal);
            // CSV не хранит тип ячейки: формулоподобный текст делаем литералом, исходник оставляем в JSON.
            var literals = fields.Where(x => x.Value.TrimStart() is { Length: > 0 } s && s[0] is '=' or '+' or '-' or '@').ToDictionary();
            Line(columns.Select(x => x == "table" ? table.Name : x == "literalValues" ? literals.Count == 0 ? "" : JsonSerializer.Serialize(literals)
                : (literals.ContainsKey(x) ? "'" : "") + fields.GetValueOrDefault(x, "")));
        }
        var encoding = new UTF8Encoding(true); return encoding.GetPreamble().Concat(encoding.GetBytes(text.ToString())).ToArray();
        void Line(IEnumerable<string> values) => text.AppendJoin(',', values.Select(x => "\"" + x.Replace("\"", "\"\"") + "\"")).Append("\r\n");
    }
}

public sealed class SessionExportService(IArchiveFileSystem files)
{
    public async Task ExportAsync(SessionArchive archive, SessionExportFormat format, string path, CancellationToken ct = default, SessionExportContent content = SessionExportContent.Full)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        string expected = format == SessionExportFormat.Csv ? ".csv" : ".xls";
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid export path or extension.");
        byte[] bytes = await Task.Run(() =>
        {
            var document = SessionExportProjection.Create(archive, content);
            return format == SessionExportFormat.Csv ? SessionCsvWriter.Write(document, ct) : BinaryXlsWriter.Write(document, ct);
        }, ct).ConfigureAwait(false);
        await files.WriteBytesAtomicallyAsync(path, bytes, ct).ConfigureAwait(false);
    }

    public static string FileName(Session session, SessionExportFormat format, SessionExportContent content = SessionExportContent.Full)
    {
        string safe = new(session.ScenarioSnapshot.Name.Where(x => !Path.GetInvalidFileNameChars().Contains(x) && !char.IsControl(x)).Take(60).ToArray());
        safe = safe.Trim().TrimEnd('.'); if (safe.Length == 0) safe = "Session";
        return $"{safe}-{session.CreatedUtc:yyyyMMdd-HHmmss}-{session.Id.ToString("N")[..8]}-{Guid.NewGuid().ToString("N")[..8]}{(content == SessionExportContent.Moments ? "-moments" : "")}.{format.ToString().ToLowerInvariant()}";
    }
}
