---
title: 利用状況イベント（POST /dashboard/events）の発火側を BFF に置く
type: spec
status: done
related_ids:
  - FR-03
  - FR-04
  - FR-07
  - FR-10
  - UC-01
  - UC-02
  - UC-05
  - SC-01
  - SC-10
  - ADR-0002
  - ADR-0006
  - ADR-0027
  - ADR-0029
  - ADR-0030
  - ADR-0044
  - IADR-0215
  - IADR-0299
  - IADR-0336
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - "02_requirements/01_requirements.md FR-10（利用状況・検索傾向・回答品質の可視化）"
  - "05_screens/01_screens.md §SC-10 KPI カードの確定（Q25〜Q27。利用状況は件数・一意利用者数は採らない）"
  - "07_adr/ADR-0006_observability-otel-prom-loki.md §結果（ログに本文・機密情報を出力しない）"
  - "07_adr/ADR-0044_llm-usage-metrics-and-pricing-table.md 決定 1（利用者識別子を属性にしない）"
---

# 仕様書: 利用状況イベントの発火側（issue #1103）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（可視化）／付随して FR-03・FR-04・FR-07（発火する経路そのもの）
- ユースケース（UC）: UC-05（関連要求表の FR-10 → UC-05）／UC-01・UC-02（発火点の経路）
- 画面（SC）: SC-10（運用ダッシュボード）／SC-01（発火の起点となる画面操作）
- 関連 ADR: ADR-0006（可観測性）・ADR-0002（DB per service）・ADR-0027 / ADR-0030（メッセージング）・ADR-0029（gRPC / REST の使い分け）・ADR-0044（計測に利用者識別子を持ち込まない）
- 計画書リンク: 隣接クローン `../project-planning/projects/microservices-platform/`（読み取り専用）

## 目的・背景

`POST /dashboard/events`（受け口・永続化・集計・BFF の口・画面）は実装済みだが、**投入する製品コードが 1 本も無い**。
その結果 `totalSearches` / `totalAnswers` / 検索傾向は恒久的に 0 であり、画面は「利用が無かった」と
「一度も測っていない」を区別できない。本作業は**発火側だけ**を入れる。

## 対象範囲

- 対象: BFF（`Knowledge.Bff.Endpoints`）から `POST /dashboard/events` を発火する経路の新設、送出の非同期化と fail-open、その計器（メトリクス＋ログ）、テスト、実測。
- 対象外:
  - 受け口・集計・BFF の `/bff/dashboard/summary`・SC-10 の画面（すべて実装済み。触らない）。
  - `UsageEvent.UserId` の保持そのもの（後述「計画書との差異」。計画の裁定が要る）。
  - `#443` のナレッジ健全性指標（別の穴。受け口も認可も違う）。
  - `#546` / `#1090`（可観測性バックエンドの構成）。

## 母集合（発火すべき箇所）

**受信側が受け付けるイベント種別の側から引いた。** `Knowledge.Contracts.Dtos.UsageEventType` の値域は
`search` / `answer` の 2 値だけである（`IsValid` が閉じている）。したがって母集合は
**(A) 利用者の意思で検索を実行する製品コード**と **(B) AI 回答を生成する製品コード**の全体である。

走査（追跡下・`--exclude-dir=ai-stock-trading`・テストを除く。2026-09-02・`develop` `2211ee77`）:

```console
$ grep -rn 'PostAsJsonAsync("/search\|PostAsync("/search\|"/search/' --include=*.cs src/ | grep -v "Tests/"
src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/SearchBffEndpoints.cs:66      /search
src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/SearchBffEndpoints.cs:129     /search/attribute-values
src/knowledge/backend/Services/AiAnalysisService/Infrastructure/ExternalServices/RagOrchestrator.cs:203  /search

$ grep -rn 'MapPost("/ask\|MapPost("/analyze\|"/analysis/' --include=*.cs src/ | grep -v "Tests/"
src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/AnalysisBffEndpoints.cs:24,37,47,60,70,81
src/knowledge/backend/Services/AiAnalysisService/Features/Analysis/{Ask,Analyze,AskStream}/Endpoint.cs
```

| # | 経路 | 種別 | 発火 | 理由 |
| --- | --- | --- | --- | --- |
| 1 | `POST /bff/search` | `search` | **する** | UC-01 / SC-01 の横断検索そのもの。利用者の資格情報がここに在る |
| 2 | `POST /bff/analysis/ask` | `answer` | **する** | UC-01 / SC-01 の RAG 回答 |
| 3 | `POST /bff/analysis/ask/stream` | `answer` | **する** | 2 と同じ回答生成の SSE 版。SPA が既定で使う経路であり、落とすと回答がほぼ数えられない |
| 4 | `POST /bff/analysis/analyze` | `answer` | **する** | FR-07 / SC-08 の分析も **LLM が根拠つきの回答（`AiAnswerDto`）を生成する**。契約の `answer` は「AI 回答生成」であって「SC-01 の質問」に限られていない。落とすと総回答数が実際の生成回数と食い違う |
| 5 | `POST /bff/attribute-values` | — | **しない** | 対象範囲フィルタの**候補一覧**であり検索ではない（`/search/attribute-values` は値集合の照会）。数えると検索件数が画面操作で膨らむ |
| 6 | `RagOrchestrator` → `POST /search` | — | **しない** | 2〜4 の回答生成が内部で行う retrieval である。数えると 1 回の質問が `answer` 1 件 ＋ `search` 1 件になり、**利用状況の 2 系列が二重計上で歪む** |
| 7 | `RetrievalService` `POST /search`（後段そのもの） | — | **しない** | ここへ置くと 6 と区別できない（呼び出し元が RAG か利用者かを後段は知らない）。加えて BFF を迂回した呼び出しには利用者の主体が無い |
| 8 | `AiAnalysisService` `POST /analysis/{ask,ask/stream,analyze}`（後段そのもの） | — | **しない** | 発火点を BFF に一本化する（IADR-0336 決定 1）。後段にも置くと 2〜4 と二重に数える |
| 9 | MCP ツール `retrieval.search_documents` | — | **しない**（不能） | **申告だけで実体が無い。** 宣言先 `/internal/mcp/search_documents` を実装したコードは 0 件である。陽性対照として、同じ走査で `/internal/mcp-tools`（申告の口）は 3 サービスに実装が見つかる。加えて MCP はサービスアカウント実行であり、`RequireAuthorization()` な受け口に載せる利用者主体を持たない |
| 10 | SPA（`src/*/frontend`） | — | **しない** | issue #1103 が明示的に除外（利用者が計測を止められ数が信頼できない） |
| 11 | 文書閲覧（`/bff/documents/*`）・グラフ（`/bff/graph/*`）等 | — | **しない** | 受け口の値域が `search` / `answer` の 2 値で、閲覧の種別が無い。**種別を増やすのは契約の変更であり計画の裁定が要る** |

走査の陽性対照: 上の grep は「発火すべきだが今は無い」箇所（1〜4）を実際に列挙している。
`grep -rn "dashboard/events" --include=*.cs src/ | grep -v "DashboardService/Features/Dashboard"` は
テストとコメントしか返さない（= 発火側が本当に 0 本である）ことを、実装前に確認済みである。

## 設計

### 発火点 —— BFF（案 b）

利用者主体は受け口が `HttpContext.User.Identity.Name` から取る。BFF は利用者の `Authorization` を
既に後段へ伝播しており（検索・回答とも）、**同じヘッダをそのまま受け口へ運べば主体が乗る**。
後段（RetrievalService / AiAnalysisService）へ置く案は、母集合 6・7・8 のとおり二重計上と
主体の運搬という 2 つの問題を新たに作る。詳細と却下理由は IADR-0336。

### 送出方式 —— HTTP（名前付き `HttpClient`）

受け口は `POST /dashboard/events`（HTTP・利用者 JWT 必須）である。BFF から後段への呼び出しは
**すべて `IHttpClientFactory` の名前付きクライアント**であり、Refit はこの層で使われていない
（`DashboardService` クライアントは `Platform.Bff/Program.cs` に既に在る）。既存の形に揃える。
Wolverine を採らない理由は IADR-0336 決定 2（要旨: BFF も DashboardService も Wolverine ホストではなく、
ブローカ経由にすると受け口が**自己申告の userId** を信じることになり認証が外れる）。

### 同期性 —— 有界キュー ＋ 常駐ドレイン（要求の応答経路に載せない）

```
/bff/search  ──(2xx を得た後)──> UsageEventReporter.Report(...)   ← 同期・O(1)・例外を投げない
                                          | Channel(capacity=1024 / TryWrite)
                                          v
                                 UsageEventDispatcher (BackgroundService)
                                          | POST /dashboard/events（5 秒上限）
                                          v
                                   DashboardService
```

- 検索の応答時間（NFR 検索 p95 1.5s）に計測の往復を載せない。`Report` は `TryWrite` だけを行う。
- 溢れは `TryWrite` が false を返すので捨て、**捨てたことを計器に載せる**（`dropped`）。`DropWrite` は使わない —— 溢れても true が返り、捨てたことを数えられなくなる。
- 停止要求時は列に残ったぶんを捨てる（計測で停止を遅らせない。利用状況の 1 件は再送に値しない）。
- 送出のたびに 5 秒の上限を掛ける（`HttpPrivateNoteNotifier` / `HttpKnowledgeHealthReporter` と同値）。

### 失敗の扱い —— fail-open ＋ 4 結末の計器

`UsageEventMetrics`（`usage.event.dispatch.total`）の属性は **`usage.event.type` と `usage.event.outcome` だけ**。
結末は `sent` / `rejected`（非 2xx）/ `unreachable`（到達不能・タイムアウト・想定外の例外）/ `dropped`（キュー溢れ）。
`rejected` / `unreachable` / `dropped` はエラーログにも落とす。**利用者識別子と検索語はログにも計器にも出さない**
（ADR-0006 §結果「ログに本文・機密情報を出力しない」／ADR-0044 決定 1）。

送出は本処理の後段にあり、`Report` は例外を投げないため、**計測の失敗で検索・回答が失敗することはない**。

### 送るフィールド —— 必要最小限

`UsageEventRequest(EventType, Query)` の 2 つだけ。

- `search`: `Query` に検索語を載せる（FR-10 の検索傾向の集計元。受け口が 512 文字へ切り詰める）。
- `answer`: **`Query` を載せない**（受け口が `answer` では捨てる値であり、質問文を経路とログに晒す理由が無い）。
- 利用者は送らない（受け口が JWT から解決する）。文書 ID・出典・結果件数も送らない。

## 受け入れ基準

- [ ] 発火点・同期性・失敗時の扱い・`private-note` の扱いが IADR に残っている（論点 1〜4 すべて）
- [ ] 検索を 1 回実行すると `UsageEvents` に `search` の行が 1 件増える
- [ ] 回答（RAG）を 1 回実行すると `answer` の行が 1 件増える
- [ ] 計測経路が失敗しても検索は成功し、落としたことが観測できる（メトリクス ＋ ログ）
- [ ] 発火の行を消すと落ちるテストがある（変異試験）
- [ ] `dotnet build` / `dotnet test`（knowledge・platform）が通る
- [ ] `docs/api/openapi.yaml` に差分が出ない（BFF に口を増やさないため）

## テスト方針

`src/platform/backend/Bff/Platform.Bff.Tests/`（新規 `UsageEventDispatchTests.cs`）。
`Platform.Bff.Tests` は **#1063 の宣言ファイル領域（`src/*/backend/Services/*/Tests/**`）の外**であり、
`Tests/Features/` への移送対象ではないので、既存の平置き構成に合わせる。既存テストファイルへの変更は
`BffTestFactory.cs` のスタブ分岐追加だけに留める（`/dashboard/events` を受けて記録する分岐）。

| # | テスト | 固定するもの |
| --- | --- | --- |
| 1 | `/bff/search` 成功 → 受け口に `search` ＋ 検索語が届く | 発火（変異試験の対象） |
| 2 | `/bff/analysis/ask` 成功 → `answer` が届き、**質問文は届かない** | 発火 ＋ 最小フィールド |
| 3 | `/bff/analysis/ask/stream` 成功 → `answer` が届く | 発火（SSE 経路） |
| 4 | `/bff/analysis/analyze` 成功 → `answer` が届く | 発火 |
| 5 | 受け口が 500 を返す → 検索は 200 のまま | fail-open |
| 6 | 受け口が到達不能（例外） → 検索は 200 のまま | fail-open |
| 7 | 空クエリ・スコープ無し・後段非 2xx → **発火しない** | 陰性対照（実行されていない検索を数えない） |
| 8 | 受け口へ利用者の `Authorization` が伝播する | 主体の解決 |

## 計画書との差異

- 差異: **あり（2 件。いずれも本作業では直さず記録する）**
  1. **`UsageEvent.UserId` を保存しているが誰も読まない。** 計画 §SC-10 の裁定 Q27 は
     一意利用者数を採らない理由として「一意集計には利用イベントへ利用者識別子を持たせる必要があり、
     『誰がいつ何回検索したか』の記録が残る。保持期間・目的外利用の禁止・開示請求への対応という統制が
     新たに要る」と述べている。受け口はその裁定より前に書かれており、`UserId` を列に持つ。
     **発火側を入れると、計画が避けると決めた記録が実際に溜まり始める。**
     列の除去は移送を伴う受け口側の設計変更であり、保持の是非は計画の裁定事項である → 環流する。
  2. **検索傾向は検索語を素で運用者へ見せる。** `private-note` の除外規則（2026-08-02 確定）は
     **文書**の集計についての規則で、検索語は文書ではなく `doc_scope` を持たないため機械的に適用できない。
     最小件数によるしきい値秘匿は集計側の設計変更であり計画の裁定が要る → 環流する。

## 未決事項

- 上記 2 件の環流（planning への issue）。本 PR は発火側の実装で閉じ、環流は別に行う。
