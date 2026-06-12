namespace SurveyAnalysis.Models;

// How a data field is analyzed after OCR. Mirrors the three choices shown in the
// project design screen: なし / トピック割り当て / 感情極性分析.
public enum AnalysisMethod
{
    None,       // なし
    Topic,      // トピック割り当て
    Sentiment   // 感情極性分析
}
