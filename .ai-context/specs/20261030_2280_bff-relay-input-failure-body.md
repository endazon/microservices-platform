---
title: 作業仕様書 — 利用者の入力を受ける BFF の口は後段の失敗の本文を透過し、読み取りの口は状態コードだけにする（#2109 の残余。#2280）
type: spec
status: done
related_ids: [FR-01, FR-03, FR-04, FR-07, FR-08, FR-17, NFR-06, ADR-0074, ADR-0115]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2280"
---

# 作業仕様書 — BFF の失敗の本文の透過（#2280）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `7c71742d9`）。

## (a) 後段の `{ error }` 形の 400 —— サーバ側は変えない（判断）

- 画面に理由が出ない、という利用者側の問題は解消済みである。MSP#2290（#2122）の 1 つ目のコミットで、SPA の `parseProblemDetails` が `{ "error": "<文>" }` を最後の手段として読むようになった。
  - 条件は「400 / 409 のとき」「RFC 7807 が無いとき」「識別子の形でない文のとき」の 3 つである。
  - SC-06 で `ConnectionUriPolicy` の理由が出ることは `apiClient.test.ts` が固定している（issue のコメント 2026-10-26）。
- 判断: **後段 DataSourceService の `{ error }` 形は RFC 7807 へ寄せない。** 理由は 3 つある。
  1. 受け入れ基準 (a)（SC-06 に理由が出る）は、クライアント側で満たされている。
  2. 形を変えると `UpdateDataSourceResponseContractTests` が固定する契約が変わる。
  3. 他の後段（分析・評価・グラフ・記録）も同じ `{ error }` 形で入力検証を返している。1 つだけ寄せると、後段の間で形が割れる。
- 寄せる場合は、全後段の `{ error }` を一括で RFC 7807 へ移す別の変更になる。本 issue の射程外である。

## (b) 他の BFF の口 —— 分類

基準は「**利用者の入力を受ける口は、失敗の本文を透過する**」「**読み取りの口は状態コードだけ**」である。読み取りの口では、後段が理由の本文を持たず、画面は状態コードで表示を分ける。

実装は、共用の `BffFailureRelay.RelayAsync`（`DataSourceBffEndpoints.RelayFailureAsync` の実体を移したもの）に寄せる。

| ファイル | 口 | 後段の失敗の本文 | 扱い |
| --- | --- | --- | --- |
| `AnalysisBffEndpoints.cs` | POST `/bff/analysis/ask` | いまは無い | **透過**（入力を受ける） |
| 同 | POST `/bff/analysis/analyze` | 400 `{ error }`（入力検証） | **透過**（SC-08 は `toMessages` で理由を出す） |
| `FeedbackBffEndpoints.cs` | POST `/bff/feedback` | 400 `{ error }`（入力検証） | **透過** |
| 同 | GET `/bff/feedback/stats` | 無い | 状態コードだけ（読み取り） |
| `ConversionBffEndpoints.cs` | GET 一覧・個別・図の一覧 | 404 は本文なし | 状態コードだけ（読み取り）。再変換・補正・画像の 4xx は既に透過している |
| 同 | `:174` | — | 透過の helper の中の「本文が空なら状態コードだけ」の分岐であり、対象外 |
| `GraphBffEndpoints.cs` | `ProxyAsync`（グラフ・近傍・カタログ・提案の GET） | 近傍の 400 `{ error, message }`。404 は `GraphEndpoints.NotFound()` の 1 か所で本文なし | **透過**（近傍の深さは利用者の入力）。404 は本文が無いので、存在の秘匿は変わらない |
| `SearchBffEndpoints.cs` | POST `/bff/search`（`:90`） | いまは無い | **透過**（入力を受ける） |
| 同 | `:257`（タグ辞書の GET） | 無い | 状態コードだけ（読み取り） |
| `DashboardBffEndpoints.cs` | `:65`・`:70`（集計の GET） | 無い | 状態コードだけ（読み取り） |
| `DataSourceBffEndpoints.cs` | 一覧・個別・DELETE | 無い | 状態コードだけ（#2279 で意図して残した） |

### 母集合（規則 9・10）

- **規則 9**: `git grep -n "Results.StatusCode((int)" -- src/knowledge/backend/Bff src/platform/backend/Bff` で 17 件を数えた（上の表のとおり）。
  - platform 側の BFF に当たりは無い。platform の中継（`UserAdminBffEndpoints.Proxy`・`AuthzBffEndpoints.Proxy`）は、既に全応答を透過している。
- **規則 10**: 次の 2 か所を、この変更に合わせて書いた。
  - 通信仕様書 `docs/api/BFF_bff-surface.md` の 4 行と、透過の範囲の注記
  - `DataSourceBffEndpoints` のコメント（実体の移動）

## 受け入れ基準 → 試験（`BffFailureRelayEndpointTests`）

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| (b) 透過する口は、後段の 400 の本文を状態コード・content-type とも透過する | `後段の400の本文を透過する`（5 口） | 5 件赤（実測） |
| 失敗の注入が当たっていることの対照 | `後段が成功すれば従来どおり`（5 口） | 緑 |
| (a) SC-06 に理由が出る | MSP#2290 の `apiClient.test.ts`（クライアント側で満たされた） | — |
