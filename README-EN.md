# SurveyAnalysis (アンケート分析)

*Read this in other languages: [English](README-EN.md) · [日本語](README-JA.md)*

A Windows desktop application for analyzing survey responses. Bring in answers
from CSV files or from scanned paper forms (via vision-model OCR), then explore
them through a dashboard, sentiment and topic analysis, and cross-tabulations.

> **Language note:** The application UI is currently available in **Japanese
> only**. English localization is planned for a future release.

## What it does

- **Projects & schema** — Define a survey as a set of fields, each with a data
  type (name, gender, address, phone, e-mail, date, choice, number, short text,
  free text) and an optional analysis method (sentiment polarity or topic
  assignment).
- **Import**
  - **CSV** — Load existing responses from a spreadsheet (UTF-8 or Shift-JIS).
  - **Scanned forms** — Read filled-in paper questionnaires with an
    OpenAI-compatible vision model. Every image is OCR'd into a staging area and
    shown on a **proofreading screen** (image on the left, editable read values
    on the right) — nothing reaches your data until you confirm it.
- **Dashboard** — Total responses, negative count, average sentiment score,
  topic counts, sentiment distribution, and a recent-responses list.
- **Analysis slices (切り口)** — Break the results down by period, weekday,
  region, topic, or choice, and cross-tabulate each axis against any question.
  The sentiment-trend chart lets you click a point to narrow the period.
- **LLM-assisted analysis** — Sentiment polarity analysis and topic assignment
  run through an OpenAI-compatible chat model.
- **Date-range scoping** — A Google-Analytics-style period picker (today,
  yesterday, last 7 / 30 / 60 days, or a custom range).
- **Export** — CSV export and a monthly report as PDF.
- **Privacy by default** — Personal-information fields (name, gender, address,
  phone, e-mail) and stored secrets (API key, SMTP / Gmail passwords) are
  encrypted at rest with Windows DPAPI. All data lives in a local SQLite
  database; nothing leaves the machine except the requests you make to the
  LLM API you configure.

## Tech stack

- **.NET 8 / C# / Windows Forms** — desktop UI, targets Windows 10/11
- **SQLite** (`Microsoft.Data.Sqlite`) — local storage
- **CommunityToolkit.Mvvm** — view models in the UI-independent core
- **OpenAI-compatible vision + chat API** — OCR, sentiment, topic assignment
- **QuestPDF** — monthly report generation
- **Windows DPAPI** (`System.Security.Cryptography.ProtectedData`) — encryption
  of secrets and personal information at rest

## Project layout

| Project | Target | Role |
| --- | --- | --- |
| `src/SurveyAnalysis.Core` | `net8.0` | Models, view models, data access, and LLM consumers — UI-independent and unit-tested. |
| `src/SurveyAnalysis.WinForms` | `net8.0-windows` | The Windows Forms desktop application. |
| `tests/SurveyAnalysis.Tests` | `net8.0` | xUnit tests over the core. |

There is no `.sln`; build each project by path.

## Requirements

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- An OpenAI-compatible API key (only needed for the OCR, sentiment, and topic
  features) — set it in **Settings → LLM** inside the app.

## Build & run

```powershell
# Build
dotnet build src\SurveyAnalysis.WinForms\SurveyAnalysis.WinForms.csproj -c Debug

# Run
dotnet run --project src\SurveyAnalysis.WinForms\SurveyAnalysis.WinForms.csproj

# Test (core only — fast, no UI)
dotnet test tests\SurveyAnalysis.Tests\SurveyAnalysis.Tests.csproj
```

The local database is created on first run at
`%LOCALAPPDATA%\SurveyAnalysis\surveyanalysis.db`.

## Build a standalone executable

Produce a single, self-contained `.exe` that runs with **no separate DLLs and no
installed .NET runtime** (native libraries are extracted on first run):

```powershell
dotnet publish src\SurveyAnalysis.WinForms\SurveyAnalysis.WinForms.csproj -c Release -r win-x64 -p:PublishSingleFile=true
```

The result is `src\SurveyAnalysis.WinForms\bin\Release\net8.0-windows\win-x64\publish\SurveyAnalysis.exe`.
Because it bundles the runtime, the file is large (~150 MB); for a small binary
that instead relies on an installed .NET 8 runtime, drop `-r win-x64` and use a
framework-dependent single file.

## Documentation

See the operation manual: [English](docs/MANUAL-EN.md) · [日本語](docs/MANUAL-JA.md).

## License

Released under the [MIT License](LICENSE).

## Status

This is a working prototype under active development. Interfaces and storage may
still change.
