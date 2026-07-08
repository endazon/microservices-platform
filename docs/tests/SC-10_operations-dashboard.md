---
title: SC-10 運用ダッシュボード テスト仕様書
type: test-spec
status: draft
related_ids:
  - SC-10
  - UC-05
  - FR-10
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../screens/SC-10_operations-dashboard.md"
  - "../specs/20260708_issue-136_sc10-operations-dashboard.md"
  - "../adr/IADR-0035_frontend-role-based-nav-and-existence-hiding.md"
---

# テスト仕様書: SC-10 運用ダッシュボード

> 計画の受け入れ基準（Issue #136）と UC-05 のフローをテストケースへ写像する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10
- ユースケース（UC）: UC-05
- 受け入れ基準の所在: Issue #136 ／ `docs/specs/20260708_issue-136_sc10-operations-dashboard.md`
- 計画書リンク: [05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md)

## テスト対象・範囲

- 対象: SC-10 画面（`features/sc10-operations`）と、本画面で導入する基盤部品（`roles`・`RequireRole`・ロール別ナビ・`opsLinks`）。
- 対象外: BFF `/bff/dashboard/summary` のサーバ側テスト（既存）。Grafana/Jaeger/Kiali 自体。

## テスト観点

- 正常系: 管理者でサマリ（総数・満足率・利用状況・傾向）と外部ツール導線・SC-11 導線が表示される。
- 認可・存在秘匿: 権限外はナビに「運用」が出ない／`/ops` 直接遷移で `NotFound`（存在を示さない）。
- API 異常系: 403（forbidden）・404（notFound）・5xx/network（error）・loading の各表示。
- 実行時 config: `opsLinks` 未設定時は外部リンクを描画しない。設定時のみ描画。
- ロール読み取り: `realm_access.roles` を access_token から復号。復号不能時は空（フェイルクローズ）。
- E2E スモーク（バックエンド不要）: 未認証 `/ops` → `/login`。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-01 | `platform-admin`、summary=200 | `/ops` 表示 | 総数・満足率・利用状況・傾向が表示 | サマリ表示 | 自動(単体) |
| T-02 | opsLinks に Grafana/Jaeger 設定 | `/ops` 表示 | Grafana/Jaeger リンク表示、Kiali 未設定は非表示 | 外部導線 | 自動(単体) |
| T-03 | `platform-admin`＋ConfigViewer | `/ops` 表示 | 「構成ビューア →」導線が表示 | SC-11 導線 | 自動(単体) |
| T-04 | 一般利用者（ロールなし） | `/ops` 直接遷移 | `NotFound` を描画（存在秘匿・リダイレクトしない） | AdminOnly/存在秘匿 | 自動(単体) |
| T-05 | 一般利用者 | ナビ描画 | 「運用」項目が出ない | 存在秘匿 | 自動(単体) |
| T-06 | summary=403 | `/ops` 表示 | 権限なしの中立メッセージ | 権限外非表示 | 自動(単体) |
| T-07 | summary=404 | `/ops` 表示 | 利用不可メッセージ（存在秘匿と整合） | 存在秘匿 | 自動(単体) |
| T-08 | summary=500/network | `/ops` 表示 | `role="alert"` の取得失敗表示 | 異常系 | 自動(単体) |
| T-09 | access_token に realm_access.roles | `extractRealmRoles` | 該当ロール配列を返す | 認可基盤 | 自動(単体) |
| T-10 | 不正/欠落トークン | `extractRealmRoles` | 空配列（フェイルクローズ） | 認可基盤 | 自動(単体) |
| T-11 | 未認証 | `/ops` を開く | `/login` へ誘導 | ルート登録・認証ガード | 自動(E2E) |

## テストデータ

- ロール別のダミー `User`（`access_token` は `header.payload.signature` 形式で payload に `realm_access.roles` を base64url 埋め込み）。
- `DashboardSummaryDto` のダミー（totalSearches/totalAnswers/usageTrend/topSearchTerms/quality）。

## 関連仕様

- 画面仕様書: `docs/screens/SC-10_operations-dashboard.md`
- 作業仕様書: `docs/specs/20260708_issue-136_sc10-operations-dashboard.md`
- 実装 ADR: [[IADR-0035]]

## 未決事項

- なし
