using System.Linq;
using System.Text;
using SurveyAnalysis.Data;
using Xunit;

namespace SurveyAnalysis.Tests;

public class CsvFileTests
{
    [Fact]
    public void Parses_header_and_data_rows()
    {
        var csv = CsvFile.Parse(Encoding.UTF8.GetBytes("a,b,c\n1,2,3\n4,5,6\n"));

        Assert.Equal(new[] { "a", "b", "c" }, csv.Header);
        Assert.Equal(2, csv.Rows.Count);
        Assert.Equal(new[] { "1", "2", "3" }, csv.Rows[0]);
        Assert.Equal(new[] { "4", "5", "6" }, csv.Rows[1]);
    }

    [Fact]
    public void Handles_quoted_commas_and_doubled_quotes()
    {
        var csv = CsvFile.Parse(Encoding.UTF8.GetBytes("name,note\n\"Doe, John\",\"He said \"\"hi\"\"\"\n"));

        Assert.Single(csv.Rows);
        Assert.Equal(new[] { "Doe, John", "He said \"hi\"" }, csv.Rows[0]);
    }

    [Fact]
    public void Handles_newline_inside_quoted_field()
    {
        var csv = CsvFile.Parse(Encoding.UTF8.GetBytes("a,b\n\"line1\nline2\",x\n"));

        Assert.Single(csv.Rows);
        Assert.Equal(new[] { "line1\nline2", "x" }, csv.Rows[0]);
    }

    [Fact]
    public void Skips_blank_lines()
    {
        var csv = CsvFile.Parse(Encoding.UTF8.GetBytes("a,b\n\n1,2\n\n"));

        Assert.Single(csv.Rows);
        Assert.Equal(new[] { "1", "2" }, csv.Rows[0]);
    }

    [Fact]
    public void Parses_a_last_row_without_trailing_newline()
    {
        var csv = CsvFile.Parse(Encoding.UTF8.GetBytes("a,b\n1,2"));

        Assert.Single(csv.Rows);
        Assert.Equal(new[] { "1", "2" }, csv.Rows[0]);
    }

    [Fact]
    public void Decodes_utf8_with_bom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("名前,評価\n田中,5\n"))
            .ToArray();

        var csv = CsvFile.Parse(bytes);

        Assert.Equal(new[] { "名前", "評価" }, csv.Header);
        Assert.Equal(new[] { "田中", "5" }, csv.Rows[0]);
    }

    [Fact]
    public void Detects_tab_delimited()
    {
        var csv = CsvFile.Parse(Encoding.UTF8.GetBytes("a\tb\tc\n1\t2\t3\n"));

        Assert.Equal(new[] { "a", "b", "c" }, csv.Header);
        Assert.Equal(new[] { "1", "2", "3" }, csv.Rows[0]);
    }

    [Fact]
    public void Detects_semicolon_delimited()
    {
        var csv = CsvFile.Parse(Encoding.UTF8.GetBytes("a;b\n1;2\n"));

        Assert.Equal(new[] { "a", "b" }, csv.Header);
        Assert.Equal(new[] { "1", "2" }, csv.Rows[0]);
    }

    [Fact]
    public void Tab_delimited_keeps_commas_inside_fields()
    {
        // With a tab delimiter, a comma is just data — even unquoted.
        var csv = CsvFile.Parse(Encoding.UTF8.GetBytes("name\tnote\n\"Doe, John\"\tx,y\n"));

        Assert.Equal(new[] { "Doe, John", "x,y" }, csv.Rows[0]);
    }

    [Fact]
    public void Decodes_shift_jis_tab_separated()
    {
        // Mirrors a Shift-JIS 「テキスト(タブ区切り)」 export: encoding fallback + tab detection.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var shiftJis = Encoding.GetEncoding(932);
        var bytes = shiftJis.GetBytes("記入日\t都道府県\t氏名\n2024/4/15\t東京都\t佐藤一\n");

        var csv = CsvFile.Parse(bytes);

        Assert.Equal(new[] { "記入日", "都道府県", "氏名" }, csv.Header);
        Assert.Equal(new[] { "2024/4/15", "東京都", "佐藤一" }, csv.Rows[0]);
    }

    [Fact]
    public void Decodes_shift_jis_when_not_valid_utf8()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var shiftJis = Encoding.GetEncoding(932);
        var bytes = shiftJis.GetBytes("名前,評価\n田中,とても満足\n");

        var csv = CsvFile.Parse(bytes);

        Assert.Equal(new[] { "名前", "評価" }, csv.Header);
        Assert.Equal(new[] { "田中", "とても満足" }, csv.Rows[0]);
    }
}
