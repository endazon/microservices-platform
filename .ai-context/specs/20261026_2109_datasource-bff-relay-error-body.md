---
title: 作業仕様書 — BFF のデータソースの登録・更新・部分更新・手動同期は、後段の失敗の状態コードと本文をそのまま返し、保存しない理由を SC-06 へ届ける（#2109）
type: spec
status: done
related_ids: [FR-01, FR-05, UC-04, SC-06, ADR-0074, ADR-0115, IADR-0039, IADR-0472]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2109"
---

# 作業仕様書 — データソースの BFF で後段の失敗本文を透過する（#2109）

> 本仕様書は実装着手前に作成した（着手 2026-10-26）。親は #2047（指摘 M-12）。

## 現状（実測）

- `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DataSourceBffEndpoints.cs` の POST `/`・PUT `/{id}`・PATCH `/{id}`・POST `/{id}/sync` は、
  後段が 2xx 以外を返すと `Results.StatusCode(...)` だけを返し、本文を捨てる。
- 後段（DataSourceService）は、検証に通らない写像・部門を 400 の RFC7807（`ValidationProblem`。`errors`）で返す（計画 ADR-0074 決定 4 / ADR-0115 決定 5・IADR-0472）。
  確認先を引けないときは 502 と `message`。
- SPA（`apiClient.ts` の `apiRequest`）は 400 / 409 の本文から `errors` / `detail` / `title` / `message` を取り出して `ApiError.details` に載せる。
  BFF が本文を捨てるので、SC-06 は「入力内容に誤りがあります。」しか出せない。
- SC-06 の試験は `ApiError` を直接作って `apiRequest` のモックから投げており、BFF の応答の形を通っていない。BFF の試験に 400 の本文の場合が無い。
- 対照: `DocumentBffEndpoints.cs` の `RelayAsync`（状態コード・content-type・本文をそのまま返す）。

## 方針

- 4 つの書き込みの口（POST・PUT・PATCH・sync）は、後段が 2xx 以外を返したら、**状態コード・content-type・本文をそのまま返す**（`RelayAsync` と同じ形。
  `DataSourceBffEndpoints` の中に 1 つ置く）。2xx の扱い（201 の組み立て・空の本文で 502）は変えない。
- 読み取り（一覧・個別）と無効化（DELETE）は変えない（本件の射程は「保存しない理由」）。
- 後段の `{ "error": "..." }` 形の 400（`ConnectionUriPolicy`・更新の入力検証）は、BFF は透過するが、SPA の `parseProblemDetails` は `error` を読まないので画面には理由が出ない。
  `{ error }` の形は `UpdateDataSourceResponseContractTests` が固定している契約であり、本件では変えない（残余。下記）。

## 受け入れ基準

- [x] BFF: 後段が POST・PUT・PATCH で 400 と `ValidationProblem` の本文を返したとき、BFF は同じ状態コード・`application/problem+json`・本文を返す。修正前は本文が空で赤
- [x] BFF: 後段の 502 と `message` の本文も透過する（確認先を引けない）。sync の 4xx も透過する
- [x] BFF ★陽性対照: 2xx の応答（201 の組み立て・200）は従来どおり
- [x] SC-06: BFF の実際の 400 本文（`fetch` の応答。`apiRequest` の本物を通す）から理由を表示する。修正前の BFF の応答（本文なしの 400）では理由が出ない（陰性対照）
- [x] テスト仕様書に足す

## 母集合（規則 9）

`git grep -n "Results.StatusCode((int)resp.StatusCode)" -- src/knowledge/backend/Bff`（BFF で後段の失敗の本文を捨てる形）。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `DataSourceBffEndpoints.cs` の POST・PUT・PATCH・sync | **直す** | 本件 |
| `DataSourceBffEndpoints.cs` の GET（一覧・個別）・DELETE | 外す | 保存しない理由の経路ではない。読み取りの失敗は状態コードだけで足りる（SC-06 は 404/502 を中立に出す） |
| `AnalysisBffEndpoints.cs:48,82`・`FeedbackBffEndpoints.cs:49,79`・`ConversionBffEndpoints.cs:53,71,108,174`・`GraphBffEndpoints.cs:288`・`SearchBffEndpoints.cs:257` | 外す（報告のみ） | 同じ形だが、データソースの「保存しない理由」とは別の経路（分析・評価・変換・グラフ・検索）。本文を要る画面かは各々で判断が要る。親 #2047 の射程外 |

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。

## 残余

- `{ "error": "..." }` 形の 400 は画面に理由が出ない（SPA が `error` を読まない）。SPA の読み方を広げるか後段の形を揃えるかは別の判断であり、本件では変えない。
