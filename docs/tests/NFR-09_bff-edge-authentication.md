---
title: エッジ（BFF）認証の担保 テスト仕様書
type: test-spec
status: draft
related_ids:
  - NFR-09
  - FR-03
  - FR-04
  - FR-06
  - FR-07
  - UC-01
  - UC-02
  - UC-03
  - SC-01
  - SC-03
  - SC-05
  - SC-08
  - ADR-0004
  - IADR-0009
  - IADR-0039
  - IADR-0044
  - IADR-0156
  - IADR-0160
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR-09: 認証・認可)"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md (SC-05 の閲覧ロール)"
---

# テスト仕様書: エッジ（BFF）認証の担保（#656）

## 起点となる計画書（トレーサビリティ）

- **NFR-09**（Must）: 「恒久: 全 API で OIDC/JWT 認証。**暫定: エッジ（BFF）で OIDC/JWT を担保**」
- 同 §暫定運用の注記（セキュリティ）: 「暫定期は**エッジ（BFF）で OIDC/JWT 認証を担保し**、
  内部サービスはネットワーク分離を第一防御とする」
- `05_screens:124`: 「**SC-05/06/07 = 閲覧は管理者・運用者**／破壊的操作は管理者限定」
- `05_screens:126`: 利用者グループ（SC-01〜04・SC-08）は「**ABAC の権限内で全利用者が利用できる**」
- Issue: **#656**／実装 ADR: [[IADR-0160]]

## テスト対象・範囲

- 対象: `/bff/*` の**端点認可**（認証の要否とロール）。
- **対象外**: 後段サービス（RetrievalService / AiAnalysisService / DocumentService）の認可。
  **#458 が持つ。本仕様が守るのは片側だけである。**
- **対象外**: `/bff/admin/config` の 3 本。`RequireAuthorization` を意図的に使わず、
  ハンドラ内 `AuthorizeAsync` ＋ 404 で**画面の存在ごと**隠す形である（[[IADR-0009]]）。
  **認可は在る**ので「無認証」ではない。

## テスト観点

- **拒否の側**: 無認証は 401／権限外は 403。
- **通る側**: 認証済みの一般利用者が従来どおり使える（**狭めすぎていない**）。
  **拒否の側だけでは「全部拒否」でも緑になる**ため、必ず対で置く。
- **存在秘匿を壊さない**: 認証済み・権限外は従来どおり 404（[[IADR-0009]] / [[IADR-0039]] 決定 3）。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-19 | 無認証 | `POST /bff/search`・`/bff/attribute-values`・`/bff/analysis/{ask,analyze,ask/stream}` | **401** | エッジで認証を担保 | 自動 |
| T-19 | 無認証 | `GET /bff/documents`・`{id}`・`{id}/content`・`{id}/versions` | **401** | 同上 | 自動 |
| T-20 | 認証済み・**非特権ロール**（`viewer`） | `POST /bff/search` | **200** | 利用者グループは全利用者が使える | 自動 |
| T-20 | 同上 | `POST /bff/attribute-values` | **200** | 同上 | 自動 |
| T-20 | 同上 | `POST /bff/analysis/ask` | **200** | 同上 | 自動 |
| T-20 | 同上 | `GET /bff/documents/{id}`（SC-03） | **403 でも 401 でもない**（スコープ外は 404） | 出典クリックの導線を壊さない | 自動 |
| T-21 | 認証済み・非特権ロール | `GET /bff/documents`（SC-05） | **403** | SC-05 の閲覧ロール | 自動 |
| T-21 | 認証済み・**運用者** | 同上 | **200** | 狭めすぎていない側 | 自動 |
| T-21 | 認証済み・**管理者** | 同上 | **200** | 同上 | 自動 |
| T-22 | — | `node scripts/check-bff-authz-docs.js` | `/bff/*` に**無認証の端点が 0 件** | 不変条件 | 自動（CI） |
| T-22 | — | 同検査器 × `ConfigBffEndpoints` | 3 本とも `requiresAuth = true`（**誤検出しない**） | 検査器の健全性 | 自動（CI） |

> **T-20 の SC-03 だけ期待値の書き方が違う。** スコープ外・不在は **404** であり（存在秘匿）、
> テスト環境の文書の有無に依存する。**「403 でも 401 でもない」ことを固定する**——
> 要点は「ロールを足していないこと」だからである。

## 対応するテストクラス

| テストクラス | 担当するケース |
| --- | --- |
| `BffEndpointAuthenticationTests` | T-19〜T-21 |
| `scripts/scripts.repo.test.js`（`check-bff-authz-docs` の節） | T-22 |

## 変異試験

| 変異 | 落ちるもの |
| --- | --- |
| `/bff/search` の `RequireAuthorization` を外す | T-19（`/bff/search`） |
| 文書一覧の `RequireRole` を外す | T-21（非特権 403） |
| 文書読み取り群の `RequireAuthorization` を外す | T-19 の 3 件（**一覧は端点側の `RequireRole` が残るので落ちない**——期待どおり） |
| 分析群の `RequireAuthorization` を外す | T-19 の 3 件 ＋ **T-22（検査器が 3 件を報告）** |

## 関連仕様

- 作業仕様書: `../specs/20260810_issue-656_bff-endpoint-authentication.md`
- 実装 ADR: `../adr/IADR-0160_bff-edge-authentication.md` ／ `../adr/IADR-0156_bff-authz-contract-checker.md`

## 未決事項

- **後段サービスの認可は本仕様の対象外**（#458）。BFF を塞いでも**クラスタ内から後段へ直接到達する
  経路は残る**。「塞いだので安全になった」と読まないこと。
- **検査器は「認証を要求するか」までしか見ない。** 「**正しい**ロールが付いているか」は人が計画を読んで決める
  ——`x-roles` と実効ロールが**どちらも同じように間違っていれば通る**（[[IADR-0156]] の既知の限界）。
