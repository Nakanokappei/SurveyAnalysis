using System;
using System.Collections.Generic;
using System.Text;

namespace SurveyAnalysis.Data;

// Reads CSV bytes into a header row and data rows. Handles the encodings common in Japanese survey
// exports (UTF-8 with/without BOM, UTF-16, and Shift-JIS/CP932 from Excel) and RFC-4180 quoting
// (commas, doubled quotes "", and newlines inside quoted fields).
public sealed class CsvFile
{
    public IReadOnlyList<string> Header { get; }
    public IReadOnlyList<string[]> Rows { get; }

    private CsvFile(IReadOnlyList<string> header, IReadOnlyList<string[]> rows)
    {
        Header = header;
        Rows = rows;
    }

    // Parses the raw bytes of a CSV file. The first record is the header; the rest are data rows.
    // Entirely blank lines are skipped.
    public static CsvFile Parse(byte[] bytes)
    {
        var records = ParseRecords(Decode(bytes));
        if (records.Count == 0)
            return new CsvFile(Array.Empty<string>(), Array.Empty<string[]>());

        var header = records[0];
        var rows = records.GetRange(1, records.Count - 1);
        return new CsvFile(header, rows);
    }

    // Shift-JIS (CP932) needs the CodePages provider, which is registered the first time this type
    // is used. The static field guarantees registration before any decode runs.
    private static readonly Encoding ShiftJis = RegisterAndGetShiftJis();

    private static Encoding RegisterAndGetShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    // Chooses an encoding by BOM, otherwise tries strict UTF-8 and falls back to Shift-JIS — the
    // two encodings a Japanese survey CSV is realistically in.
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return ShiftJis.GetString(bytes);
        }
    }

    // RFC-4180 record splitter: walks the text once, respecting quoted fields (which may contain
    // commas, doubled quotes, and newlines). Returns one string[] per non-blank record.
    private static List<string[]> ParseRecords(string text)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var sawAnyChar = false;

        // Finishes the current field and record, then resets for the next one.
        void EndRecord()
        {
            fields.Add(field.ToString());
            field.Clear();
            // Skip records that are entirely empty (e.g. a trailing or interleaved blank line).
            var hasContent = false;
            foreach (var f in fields)
                if (f.Length != 0) { hasContent = true; break; }
            if (hasContent)
                records.Add(fields.ToArray());
            fields.Clear();
            sawAnyChar = false;
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // A doubled quote inside a quoted field is a literal quote.
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                field.Append(c); i++; continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true; sawAnyChar = true; i++;
                    break;
                case ',':
                    fields.Add(field.ToString()); field.Clear(); sawAnyChar = true; i++;
                    break;
                case '\r':
                    EndRecord();
                    i += (i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
                    break;
                case '\n':
                    EndRecord(); i++;
                    break;
                default:
                    field.Append(c); sawAnyChar = true; i++;
                    break;
            }
        }

        // Flush a final record that wasn't terminated by a newline.
        if (sawAnyChar || field.Length != 0 || fields.Count != 0)
            EndRecord();

        return records;
    }
}
