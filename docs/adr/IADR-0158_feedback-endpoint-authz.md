---
title: IADR-0158 フィードバック端点の認可を両層で塞ぎ、匿名フォールバックを消す
type: impl-adr
status: Accepted
related_ids:
  - FR-08
  - UC-01
  - SC-01
  - SC-10
  - ADR-0004
  - IADR-0010
  - IADR-0039
  - IADR-0044
  - IADR-0128
  - IADR-0156
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
---

# IADR-0158: フィードバック端点の認可（#521）

- 状態: Accepted
- 日付: 2026-08-10
- 決定者: claude（実装）

## 起点・関連

- **FR-08 / UC-01 / SC-01（投稿）・SC-10（統計）**。実装 issue: **#521**
- 作業仕様書: [20260810_issue-521](../specs/20260810_issue-521_feedback-endpoint-authz.md)

## コンテキストと課題

`/bff/feedback`・`/bff/feedback/stats`・後段 `POST /feedback`・`GET /feedback/stats` の
**4 口すべてに認可が無く、無認証で投稿も統計取得もできた**。同一ファイルの
`GET /feedback`（一覧）だけが `AdminOnly` を持つという不揃いでもあった。

### ★ issue は「裁定が要る」と書いていたが、裁定は**既に済んでいた**

#521（起票 2026-08-05）は 3 案を挙げて計画への環流を求めている。**母集合を issue 本文では
なく計画書から引き直したところ、2026-08-07 に利用者裁定が確定していた**（裁定依頼
planning#236・**案 2**）——`02_requirements:33` / `05_screens:188` / `05_screens:431`、
および受け入れ基準 `02_requirements:209-210`。

**環流も裁定依頼も不要だった。** 転記していたら、決着済みの裁定をもう一度投げていた。

## 決定 1: **両層**で塞ぐ（[[IADR-0044]] 多層防御）

BFF だけに付けると、クラスタ内から後段へ直接到達する経路が空いたままになる。
BFF（`/bff/feedback` 群と `/stats`）と後段（`POST /feedback` と `GET /feedback/stats`）の**両方**へ置く。

## 決定 2: 投稿は「**認証のみ**」でありロールを要求しない

計画は「**認証を要する**（匿名投稿は許さない）」までしか定めていない。
**書かれていない制限を足さない**——ロールまで要求すると一般利用者がフィードバックを送れなくなり、
FR-08 が成り立たない。`RequireAuthorization()`（ポリシー無し）を使い、
**「非特権ロールでも投稿できる」ことを試験で固定する**（狭めすぎていないことの側）。

## 決定 3: 統計は **admin ＋ operator**（AND 合成）

計画 `05_screens:431`「統計は運用者・管理者に限る。本画面全体の閲覧ロールと揃える」。
SC-10 の画面の閲覧ロールは #544（PR #648）で運用者へ開いてあるので、同じ線で引く。

BFF は群の `RequireAuthorization()` と `/stats` の `RequireRole(Admin, Operator)` を重ね、
**AND 合成で実効 admin ＋ operator** とする（[[IADR-0128]] 決定 1）。
**この形は #647 の検査器（[[IADR-0156]]）が解ける**ことを実測で確認済みである。

## 決定 4: `anonymous` フォールバックを**消す**

`FeedbackEndpoints.cs` は `userId = http.User.Identity?.Name ?? "anonymous"` としていた。

**この害は「無認証で投稿できる」だけではなかった。** `FeedbackDbContext` が
**`(AnswerId, UserId)` にユニーク索引**を張っている（[[IADR-0010]]）ため、
**無認証の投稿は全員が `"anonymous"` という 1 行を共有し、互いに上書きし合っていた**——
「1 利用者 1 件」の upsert が、匿名に対しては「**全匿名で 1 件**」として働く。
指標の汚染に加えて**他人の投稿の改変**にあたる。

`RequireAuthorization` を付ければ通常は到達しないが、**残すと「認可を外したときに静かに
匿名共有へ戻る」**。名前が取れなければ 401 を返す（[[IADR-0039]] 決定 3）。

> なお `FeedbackEndpoints.cs` の `［2026-08-07 / #586］` 追記が、**この是正を #521 の担当と
> 明記していた**。予告どおりの回収である。

## 決定 5 の前に: **予告の回収は全数で行う**（レビュー 🟡 を受けて拡張）

当初の実装は `/bff/*` の 2 口と `anonymous` フォールバックだけを直していた。
**しかし #586 は「#521 が持つ」と名指しした予告を live 文書 8 箇所へ残していた**
（同 PR の作業仕様書 §実測 7 が一覧を持つ）。レビューがそのうち 1 つ（`openapi.yaml` の内部 API）を
指摘したので、**規則どおり同型を全数走査**して残り全部を回収した:

`openapi.yaml` の内部 `/feedback`・`/feedback/stats`（`401` / `403` と根拠の書き換え）／
`FeedbackEndpoints.cs` の `:101` `:131` のコメント／`docs/api/BFF_bff-surface.md`（冒頭・
エンドポイント一覧 2 行・§未決事項 3）／`docs/functional/FR-08_answer-feedback.md`（API 表・例外フロー）／
`docs/tests/FR-08_answer-feedback.md`（T-15 / T-16 を実装済みへ。**通る側の対として T-17 / T-18 を追加**）／
[[IADR-0010]]／[[IADR-0131]] フォローアップ 4。

**母集合を `feedback` という語で引いたのが誤りだった。** 引くべきは **issue 番号**である——
先行作業は「これは #NNN が持つ」と書き残す規約なので、**番号で引けば引き継ぎ表が機械的に出る**。
`FeedbackEndpoints.cs` の予告は見つけて「予告どおりの回収」と書いていたのに、
**同じ形があと 7 箇所あることに気づいていなかった**（1 つ見つけて満足していた）。

## 決定 5: 既存の `"anonymous"` 行は**移行しない**（本 PR の射程外）

既存データに `userId = "anonymous"` の行が残り得る。**データ移行は別の資源**であり
（[[IADR-0139]]）、本番データの有無も把握していない。**申し送りとして残す。**

## 結果

- 計画の受け入れ基準 2 項目（無認証 401 / 権限外 403）を**両層で**満たす。
- **画面側の追随は不要**と実測した——投稿は orval 生成フック → `apiRequest` が Bearer を付ける経路で
  既に認証済み。`/bff/feedback/stats` を呼ぶ画面は**まだ無い**（SC-10 は `/bff/dashboard/summary` を見る）。

### 変異試験（いずれも復旧後に緑を確認）

| 変異 | 落ちる試験 |
| --- | --- |
| BFF 群の `RequireAuthorization` を外す | 投稿の 401 |
| BFF `/stats` の `RequireRole` を外す | 統計の 403 |
| 後段の `RequireAuthorization` 2 つを外す | 統計の 401・403 |
| `openapi.yaml` の `x-roles` を `[]` へ戻す | `check-bff-authz-docs`（#647）が実効ロールを名指しで報告して fail |

### ★ 投稿の 401 は**二重に守られており、単一の変異では落ちない**（正直に書く）

`POST /feedback` には `RequireAuthorization` と `userId` の null 検査という**独立した 2 つの門**がある。
実測すると:

| 変異 | 結果 |
| --- | --- |
| `RequireAuthorization` だけ外す（`userId` 検査は残す） | **緑のまま**（`userId` 検査が 401 を返す） |
| `userId` 検査だけ戻す（`RequireAuthorization` は残す） | **緑のまま**（ミドルウェアが 401 を返す） |
| **両方**外す | 落ちる |

すなわち**試験が固定しているのは結果（無認証は 401）であって、どちらの門が効いたかではない。**
これは**意図した冗長**（決定 4 の「認可を外したときに静かに戻らない」ための二重化）であり、
**試験の弱点ではない**——が、「この試験が `RequireAuthorization` の存在を証明する」とは**言えない**。
区別して記録しておく。

## 申し送り

- **既存 `"anonymous"` 行の扱い**（決定 5）。移行または削除が要るなら別 issue。
- **`GET /feedback`（一覧）の認可は据え置き**（既に `AdminOnly` で計画とも整合）。
- **`/bff/feedback/stats` を SC-10 の画面へ出す作業は未着手**。計画は統計を SC-10 で参照すると
  定めているが、画面はまだ `/bff/dashboard/summary` しか見ていない。**別の資源**であり束ねない。
