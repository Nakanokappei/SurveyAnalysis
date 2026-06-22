using System;

namespace SurveyAnalysis.Data;

// Raised by CsvFile.Parse when a CSV/TSV file can't be interpreted: an undetectable encoding, an empty
// file with no header, or rows whose field count doesn't match the header (RFC-4180 violation). The
// Message is user-facing (Japanese) so callers can show it directly to explain the cause.
public sealed class CsvFormatException : Exception
{
    public CsvFormatException(string message) : base(message) { }
}
