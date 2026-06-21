# アンケート分析 (SurveyAnalysis)

*他の言語で読む: [English](README-EN.md) · [日本語](README-JA.md)*

アンケートの回答を分析する Windows デスクトップアプリです。CSV ファイル、または
スキャンした紙のアンケート用紙（Vision モデルによる OCR）から回答を取り込み、
ダッシュボード・感情/トピック分析・クロス集計で掘り下げられます。

> **言語について:** アプリの UI は現在**日本語のみ**です。将来的に英語対応を予定して
> います。

## 主な機能

- **プロジェクトと項目定義** — アンケートを項目の集合として定義します。各項目には
  データ型（氏名・性別・住所・電話番号・メールアドレス・日付・選択肢・数値・テキスト・
  テキスト（改行あり））と、任意の分析方法（感情極性分析 / トピック割り当て）を設定。
- **取り込み**
  - **CSV** — 既存の回答を表計算ファイルから読み込み（UTF-8 / Shift-JIS 対応）。
  - **スキャン用紙** — 記入済みの紙アンケートを OpenAI 互換の Vision モデルで読み取り。
    各画像は仮テーブルへ OCR され、**校正画面**（左に画像、右に編集可能な読み取り値）で
    確認します。取り込みを確定するまで実データには一切入りません。
- **ダッシュボード** — 総回答数・ネガティブ件数・平均感情スコア・トピック別件数・
  感情極性の分布・回答一覧。
- **切り口** — 期間別 / 曜日別 / 地域別 / トピック別 / 選択肢別で集計し、各軸を質問と
  クロス集計できます。感情極性の推移グラフは、点をクリックすると期間を絞り込めます。
- **LLM 連携分析** — 感情極性分析とトピック割り当てを OpenAI 互換のチャットモデルで実行。
- **対象期間の指定** — Google アナリティクス風の期間ピッカー（今日・昨日・直近7/30/60日・
  カスタム期間）。
- **エクスポート** — CSV 出力と月次レポート（PDF）。
- **プライバシー重視** — 個人情報の項目（氏名・性別・住所・電話番号・メールアドレス）と
  保存される秘密情報（API キー・SMTP / Gmail パスワード）は Windows DPAPI で保存時に
  暗号化されます。データはすべてローカルの SQLite に保存され、設定した LLM API への
  リクエスト以外に端末から送信されるものはありません。

## 技術スタック

- **.NET 8 / C# / Windows Forms** — デスクトップ UI。Windows 10/11 対象
- **SQLite**（`Microsoft.Data.Sqlite`）— ローカル保存
- **CommunityToolkit.Mvvm** — UI 非依存コアのビューモデル
- **OpenAI 互換 Vision + Chat API** — OCR・感情・トピック割り当て
- **QuestPDF** — 月次レポート生成
- **Windows DPAPI**（`System.Security.Cryptography.ProtectedData`）— 秘密情報と
  個人情報の保存時暗号化

## プロジェクト構成

| プロジェクト | ターゲット | 役割 |
| --- | --- | --- |
| `src/SurveyAnalysis.Core` | `net8.0` | モデル・ビューモデル・データアクセス・LLM 連携。UI 非依存・ユニットテスト対象。 |
| `src/SurveyAnalysis.WinForms` | `net8.0-windows` | Windows Forms デスクトップアプリ本体。 |
| `tests/SurveyAnalysis.Tests` | `net8.0` | コアに対する xUnit テスト。 |

`.sln` はありません。各プロジェクトをパス指定でビルドします。

## 必要環境

- Windows 10 または 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- OpenAI 互換の API キー（OCR・感情・トピック機能でのみ必要）— アプリ内の
  **設定 → LLM** で設定します。

## ビルドと実行

```powershell
# ビルド
dotnet build src\SurveyAnalysis.WinForms\SurveyAnalysis.WinForms.csproj -c Debug

# 実行
dotnet run --project src\SurveyAnalysis.WinForms\SurveyAnalysis.WinForms.csproj

# テスト（コアのみ・高速・UI 不要）
dotnet test tests\SurveyAnalysis.Tests\SurveyAnalysis.Tests.csproj
```

ローカル DB は初回起動時に
`%LOCALAPPDATA%\SurveyAnalysis\surveyanalysis.db` に作成されます。

## 単体実行ファイルの作成

**別途の DLL も .NET ランタイムのインストールも不要**で動作する、自己完結の単一
`.exe` を生成します（ネイティブライブラリは初回起動時に展開されます）:

```powershell
dotnet publish src\SurveyAnalysis.WinForms\SurveyAnalysis.WinForms.csproj -c Release -r win-x64 -p:PublishSingleFile=true
```

生成物は
`src\SurveyAnalysis.WinForms\bin\Release\net8.0-windows\win-x64\publish\SurveyAnalysis.exe`
です。ランタイムを同梱するためサイズは大きめ（約 150 MB）になります。インストール済みの
.NET 8 ランタイムを利用する小さなバイナリにしたい場合は、`-r win-x64` を外して
フレームワーク依存の単一ファイルにしてください。

## ドキュメント

操作マニュアル: [日本語](docs/MANUAL-JA.md) · [English](docs/MANUAL-EN.md)。

## ライセンス

[MIT License](LICENSE) の下で公開しています。

## ステータス

開発中の実用プロトタイプです。インターフェースや保存形式は今後変わる可能性があります。
