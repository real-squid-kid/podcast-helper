using System.Collections.Immutable;
using System.Text;

namespace PodcastHelper.Infrastructure.Export;

// Узкий writer BIFF8/CFB по MS-XLS и MS-CFB: только Unicode-текст, без формул, макросов и чтения файлов.
public static class BinaryXlsWriter
{
    private const int RecordLimit = 8224;
    private const int WorkbookLimit = 128 * 1024 * 1024;
    private sealed record Sheet(string Name, ImmutableArray<string> Columns, ImmutableArray<ImmutableArray<string>> Rows);

    public static byte[] Write(ExportDocument document, CancellationToken ct = default)
    {
        var sheets = new List<Sheet>();
        foreach (var table in document.Tables)
        {
            if (table.Columns.Length is < 1 or > 256) throw new ExportLimitException("XLS allows up to 256 columns. Export CSV instead.");
            int parts = Math.Max(1, (table.Rows.Length + 65534) / 65535);
            for (int part = 0; part < parts; part++)
                sheets.Add(new(parts == 1 ? table.Name : $"{table.Name[..Math.Min(23, table.Name.Length)]} {part + 1}", table.Columns, table.Rows.Skip(part * 65535).Take(65535).ToImmutableArray()));
        }
        if (sheets.Count == 0 || sheets.Count > 255) throw new ExportLimitException("Too many XLS sheets. Export CSV instead.");
        var strings = new List<string>(); var indexes = new Dictionary<string, int>(StringComparer.Ordinal); int total = 0; long estimate = 0;
        foreach (var sheet in sheets)
        foreach (var row in sheet.Rows.Prepend(sheet.Columns))
        {
            ct.ThrowIfCancellationRequested();
            if (row.Length != sheet.Columns.Length) throw new ArgumentException("Export row width is inconsistent.");
            foreach (string value in row)
            {
                if (value.Length > 32767) throw new ExportLimitException("A text cell exceeds the XLS limit of 32,767 characters. Export CSV to keep the full text.");
                total++; estimate += 14;
                if (!indexes.ContainsKey(value)) { indexes.Add(value, strings.Count); strings.Add(value); estimate += value.Length * 2L + 8; }
                if (estimate > WorkbookLimit) throw new ExportLimitException("This session is too large for XLS export. Export CSV instead.");
            }
        }
        using var workbook = new MemoryStream(); using var writer = new BinaryWriter(workbook, Encoding.Unicode, leaveOpen: true);
        void Record(ushort type, Action<BinaryWriter> body) => WriteRecord(writer, type, body);
        Record(0x0809, w => Bof(w, 0x0005));
        Record(0x0042, w => w.Write((ushort)1200));
        Record(0x003D, w => { foreach (ushort v in new ushort[] { 0, 0, 16384, 8192, 0x0038, 0, 0, 1, 600 }) w.Write(v); });
        Record(0x0022, w => w.Write((ushort)0));
        foreach (ushort weight in new ushort[] { 400, 700 })
            Record(0x0031, w => { w.Write((ushort)200); w.Write((ushort)0); w.Write((ushort)0x7FFF); w.Write(weight); w.Write((ushort)0); w.Write(new byte[] { 0, 0, 0, 0, 5, 0 }); w.Write(Encoding.ASCII.GetBytes("Arial")); });
        // Стиль Normal и обычная текстовая ячейка, встроенный формат 49 (@).
        Record(0x00E0, w => { w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0xFFF5); w.Write(new byte[14]); });
        Record(0x00E0, w => { w.Write((ushort)0); w.Write((ushort)49); w.Write((ushort)0x0001); w.Write(new byte[14]); });
        Record(0x00E0, w => { w.Write((ushort)1); w.Write((ushort)49); w.Write((ushort)0x0001); w.Write(new byte[14]); });
        Record(0x0293, w => { w.Write((ushort)0x8000); w.Write((byte)0); w.Write((byte)0); });
        var pointers = new List<long>();
        foreach (var sheet in sheets)
        {
            if (sheet.Name.Length is < 1 or > 31 || sheet.Name.IndexOfAny([':', '\\', '/', '?', '*', '[', ']']) >= 0) throw new ArgumentException("Invalid worksheet name.");
            pointers.Add(workbook.Position + 4);
            Record(0x0085, w => { w.Write(0); w.Write((ushort)0); w.Write((byte)sheet.Name.Length); w.Write((byte)1); w.Write(Encoding.Unicode.GetBytes(sheet.Name)); });
        }
        WriteStrings(writer, strings, total, ct);
        Record(0x000A, _ => { });
        for (int index = 0; index < sheets.Count; index++)
        {
            var sheet = sheets[index]; long start = workbook.Position; workbook.Position = pointers[index]; writer.Write(checked((uint)start)); workbook.Position = start;
            Record(0x0809, w => Bof(w, 0x0010));
            Record(0x0200, w => { w.Write(0); w.Write(sheet.Rows.Length + 1); w.Write((ushort)0); w.Write((ushort)sheet.Columns.Length); w.Write((ushort)0); });
            Record(0x0081, w => w.Write((ushort)0x04C1));
            for (int col = 0; col < sheet.Columns.Length; col++)
            {
                int width = Math.Clamp(Math.Max(sheet.Columns[col].Length, sheet.Rows.Take(200).Select(x => x[col].Length).DefaultIfEmpty().Max()) + 2, 14, 48);
                Record(0x007D, w => { w.Write((ushort)col); w.Write((ushort)col); w.Write((ushort)(width * 256)); w.Write((ushort)1); w.Write((ushort)0); w.Write((ushort)0); });
            }
            int rowIndex = 0;
            foreach (var row in sheet.Rows.Prepend(sheet.Columns))
            {
                ct.ThrowIfCancellationRequested();
                Record(0x0208, w => { w.Write((ushort)rowIndex); w.Write((ushort)0); w.Write((ushort)row.Length); w.Write((ushort)255); w.Write(0u); w.Write(0x00000100u); });
                for (int col = 0; col < row.Length; col++)
                    Record(0x00FD, w => { w.Write((ushort)rowIndex); w.Write((ushort)col); w.Write((ushort)(rowIndex == 0 ? 2 : 1)); w.Write(indexes[row[col]]); });
                rowIndex++;
            }
            Record(0x023E, w => { w.Write((ushort)(index == 0 ? 0x06B6 : 0x00B6)); w.Write((ushort)0); w.Write((ushort)0); w.Write(0x0040u); w.Write((ushort)0); w.Write((ushort)100); w.Write(0u); });
            Record(0x000A, _ => { });
        }
        return Compound(workbook.ToArray(), ct);
    }

    private static void Bof(BinaryWriter writer, ushort kind)
    { writer.Write((ushort)0x0600); writer.Write(kind); writer.Write((ushort)0x0DBB); writer.Write((ushort)0x07CC); writer.Write(0x00000041u); writer.Write(0x00000006u); }
    private static void WriteRecord(BinaryWriter target, ushort type, Action<BinaryWriter> body)
    {
        using var payload = new MemoryStream(); using var writer = new BinaryWriter(payload, Encoding.Unicode, true); body(writer);
        if (payload.Length > RecordLimit) throw new ExportLimitException("XLS record is too large. Export CSV instead.");
        target.Write(type); target.Write((ushort)payload.Length); target.Write(payload.GetBuffer(), 0, (int)payload.Length);
    }
    private static void WriteStrings(BinaryWriter target, List<string> strings, int total, CancellationToken ct)
    {
        using var payload = new MemoryStream(); using var writer = new BinaryWriter(payload, Encoding.Unicode, true);
        ushort record = 0x00FC; writer.Write(total); writer.Write(strings.Count);
        void Flush()
        {
            target.Write(record); target.Write((ushort)payload.Length); target.Write(payload.GetBuffer(), 0, (int)payload.Length);
            payload.SetLength(0); payload.Position = 0; record = 0x003C;
        }
        foreach (string text in strings)
        {
            ct.ThrowIfCancellationRequested();
            if (RecordLimit - payload.Length < 3) Flush();
            writer.Write((ushort)text.Length); writer.Write((byte)1);
            int position = 0;
            while (position < text.Length)
            {
                int count = Math.Min(text.Length - position, (RecordLimit - (int)payload.Length) / 2);
                if (count > 0 && position + count < text.Length && char.IsHighSurrogate(text[position + count - 1]) && char.IsLowSurrogate(text[position + count])) count--;
                if (count == 0) { Flush(); writer.Write((byte)1); continue; }
                writer.Write(Encoding.Unicode.GetBytes(text.AsSpan(position, count).ToString())); position += count;
                if (position < text.Length) { Flush(); writer.Write((byte)1); }
            }
        }
        if (payload.Length > 0) Flush();
    }

    private static byte[] Compound(byte[] workbook, CancellationToken ct)
    {
        const uint free = 0xFFFFFFFF, end = 0xFFFFFFFE, fatSector = 0xFFFFFFFD, difSector = 0xFFFFFFFC;
        int size = Math.Max(4096, workbook.Length), dataSectors = (size + 511) / 512, fatCount = 1, difCount = 0;
        while (true)
        {
            int nextFat = (dataSectors + 1 + fatCount + difCount + 127) / 128;
            int nextDif = Math.Max(0, (nextFat - 109 + 126) / 127);
            if (fatCount == nextFat && difCount == nextDif) break;
            fatCount = nextFat; difCount = nextDif;
        }
        int directory = dataSectors, firstFat = directory + 1, firstDif = firstFat + fatCount;
        using var file = new MemoryStream(); using var writer = new BinaryWriter(file, Encoding.Unicode, true);
        writer.Write(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }); writer.Write(new byte[16]);
        writer.Write((ushort)0x003E); writer.Write((ushort)3); writer.Write((ushort)0xFFFE); writer.Write((ushort)9); writer.Write((ushort)6); writer.Write(new byte[6]);
        writer.Write(0); writer.Write(fatCount); writer.Write(directory); writer.Write(0); writer.Write(4096); writer.Write(end); writer.Write(0);
        writer.Write(difCount == 0 ? end : (uint)firstDif); writer.Write(difCount);
        for (int i = 0; i < 109; i++) writer.Write(i < fatCount ? (uint)(firstFat + i) : free);
        writer.Write(workbook); writer.Write(new byte[dataSectors * 512 - workbook.Length]);
        void Entry(string name, byte type, uint child, uint start, ulong streamSize)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(name + '\0'); writer.Write(bytes); writer.Write(new byte[64 - bytes.Length]);
            writer.Write((ushort)bytes.Length); writer.Write(type); writer.Write((byte)1); writer.Write(free); writer.Write(free); writer.Write(child);
            writer.Write(new byte[16]); writer.Write(0u); writer.Write(0ul); writer.Write(0ul); writer.Write(start); writer.Write(streamSize);
        }
        Entry("Root Entry", 5, 1, end, 0); Entry("Workbook", 2, free, 0, (ulong)size); writer.Write(new byte[256]);
        for (int i = 0; i < fatCount * 128; i++)
        {
            ct.ThrowIfCancellationRequested();
            writer.Write(i < dataSectors ? i == dataSectors - 1 ? end : (uint)(i + 1)
                : i == directory ? end : i < firstDif ? fatSector : i < firstDif + difCount ? difSector : free);
        }
        for (int d = 0; d < difCount; d++)
        {
            for (int i = 0; i < 127; i++) { int f = 109 + d * 127 + i; writer.Write(f < fatCount ? (uint)(firstFat + f) : free); }
            writer.Write(d == difCount - 1 ? end : (uint)(firstDif + d + 1));
        }
        return file.ToArray();
    }
}
