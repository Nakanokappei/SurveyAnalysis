using SurveyAnalysis.Data;
using Xunit;

namespace SurveyAnalysis.Tests;

// The address split feeding the region dimension's 都道府県 / 市区町村 hierarchy.
public class AddressParserTests
{
    [Theory]
    [InlineData("東京都新宿区西新宿1-1-1", "東京都", "新宿区")]
    [InlineData("大阪府大阪市北区梅田", "大阪府", "大阪市")]              // 政令市 collapses to the 市 level
    [InlineData("北海道虻田郡倶知安町字山田", "北海道", "虻田郡倶知安町")] // 郡部 keeps the 郡 prefix
    [InlineData("神奈川県横浜市西区みなとみらい", "神奈川県", "横浜市")]
    [InlineData("  京都府京都市  ", "京都府", "京都市")]                  // surrounding spaces trimmed
    [InlineData("東京都", "東京都", "")]                                  // prefecture only
    [InlineData("不明な住所", "（不明）", "")]                            // no known prefecture
    [InlineData("", "（不明）", "")]
    public void Parse_splits_prefecture_and_city(string address, string prefecture, string city)
    {
        var (p, c) = AddressParser.Parse(address);
        Assert.Equal(prefecture, p);
        Assert.Equal(city, c);
    }
}
