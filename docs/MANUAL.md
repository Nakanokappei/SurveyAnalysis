# SurveyAnalysis — Operation Manual

This manual walks through everyday use of the application. The UI is currently
in Japanese; each step gives the on-screen Japanese label with an English gloss
in parentheses.

> **Language note:** The application is currently Japanese-only. English
> localization is planned, at which point this manual will be updated to match.

## Contents

1. [Setup](#1-setup)
2. [Creating a project](#2-creating-a-project)
3. [Configuring fields](#3-configuring-fields)
4. [Importing responses](#4-importing-responses)
5. [The dashboard](#5-the-dashboard)
6. [Analysis slices](#6-analysis-slices)
7. [Exporting](#7-exporting)
8. [Settings](#8-settings)
9. [Data storage & privacy](#9-data-storage--privacy)
10. [Troubleshooting](#10-troubleshooting)

---

## 1. Setup

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download) on Windows 10/11.
2. Build and launch the app (see the README's *Build & run* section).
3. Open **設定 (Settings)** from the bottom of the sidebar and, under the
   **LLM** tab, paste your OpenAI-compatible **API key** and choose the model.
   This is required for the OCR, sentiment, and topic features; CSV import and
   manual editing work without it.

## 2. Creating a project

From the welcome screen (**プロジェクトを開始しましょう** — "Let's start a project"):

- **プロジェクトを作る (Create a project)** — start an empty project and define
  its fields yourself.
- **CSV からプロジェクトを作る (Create from CSV)** — pick a CSV and let the app
  infer the fields from its header, then adjust them.

A recently used project can be reopened from the list via **開く (Open)**.

## 3. Configuring fields

Open **構成 (Configure)** next to the project name in the sidebar. Each row is
one field:

- **項目名 (Field name)** — the column / question label.
- **データ型 (Data type)** — one of: 氏名 (name), 性別 (gender), 住所 (address),
  電話番号 (phone), メールアドレス (e-mail), 日付 (date), 選択肢 (choice),
  数値 (number), テキスト (short text), テキスト（改行あり） (free text).
  The first five are **personal information** and are encrypted at rest (a 🔒
  badge marks them).
- **分析方法 (Analysis method)** — なし (none), 感情極性分析 (sentiment polarity),
  or トピック割り当て (topic assignment). Sentiment/topic apply to free-text
  fields.

Choice fields can hold more than one selected option in a single cell, separated
by a semicolon `;` (for example `テレビ;インターネット`).

## 4. Importing responses

### CSV import

Use **インポート (CSV)** in the sidebar. Preview the rows, page through them, and
confirm. Both UTF-8 and Shift-JIS encodings are supported.

### Scanned-form import (OCR)

1. **画像を読み込む (Load images)** — pick one or more scanned form images, or
   **フォルダから画像を読み込む (Load images from a folder)** to read every image
   in a folder. (Requires an API key — see Setup.)
2. Each image is read with the vision model into a staging area. A paid request
   is made per image, so you are asked to confirm the batch first.
3. The **proofreading screen (校正)** opens: the scanned image on the left, the
   read values (editable) on the right. Correct anything the model misread.
   - For a **choice** field, a hint reminds you to separate multiple selected
     options with a half-width semicolon `;`.
4. For each record choose **この内容で取り込む (Import this record)** or
   **破棄 (Discard)**. Nothing is written to your data until you import it; a
   half-reviewed batch is kept safely for next time.
5. If a record exactly matches an existing response, you are asked whether to
   add it anyway, skip it, or cancel.

## 5. The dashboard

**ダッシュボード (Dashboard)** summarizes the responses in the selected period:

- **総回答数 (Total responses)**, **ネガティブ件数 (Negative count)**, and
  **平均感情スコア (Average sentiment score)**.
- **トピック別 件数 (Counts by topic)** and **感情極性の分布 (Sentiment
  distribution)**.
- **回答一覧（抜粋） (Response list, excerpt)**.

Use the **対象期間 (Target period)** picker (top right) to scope everything to a
date range.

## 6. Analysis slices

Under **切り口 (Slices)** in the sidebar, expand an axis with the chevron and
open a breakdown:

- **期間別 (By period)** — a sentiment-trend chart over time; **click a point to
  narrow the period** to that day or week.
- **曜日別 (By weekday)**, **地域別 (By region)** — tabular breakdowns.
- **トピック別 (By topic)**, **選択肢別 (By choice)** — one sub-item per question.

Each axis can be cross-tabulated against a question (axis × question). The same
**対象期間 (Target period)** picker scopes the slice.

## 7. Exporting

- **CSV エクスポート (CSV export)** — export the current view / responses as CSV.
- **エクスポート (Export)** — generate a monthly report as a PDF.

## 8. Settings

Open **設定 (Settings)** at the bottom of the sidebar:

- **LLM** — API key and model used for OCR, sentiment, and topic features.
- **メール / SMTP (E-mail / SMTP)** — Gmail / SMTP credentials for sending
  reports.

Secrets entered here are encrypted before they are stored (see below).

## 9. Data storage & privacy

- All data is stored locally in a SQLite database at
  `%LOCALAPPDATA%\SurveyAnalysis\surveyanalysis.db`.
- Personal-information fields (name, gender, address, phone, e-mail) and stored
  secrets (API key, SMTP / Gmail passwords) are encrypted at rest with Windows
  DPAPI (current-user scope), so the database file alone cannot reveal them.
- Nothing is sent anywhere except the requests you explicitly make to the LLM
  API you configured (OCR, sentiment, topic).
- To reset everything, close the app and delete the database file.

## 10. Troubleshooting

- **"画像の読み取りには OpenAI の API キーが必要です" (Image reading needs an
  OpenAI API key)** — set the key in **Settings → LLM**.
- **OCR misreads checkboxes** — correct them on the proofreading screen before
  importing; the read values are always shown for review.
- **A secret shows blank after moving machines** — DPAPI-encrypted values are
  tied to the Windows user that wrote them and cannot be decrypted elsewhere;
  re-enter the secret in Settings.
