---
title: SC-11 構成ビューア テスト仕様書
type: test-spec
status: draft
related_ids:
  - SC-11
  - FR-15
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - "../screens/SC-11_configuration-viewer.md"
  - "../adr/IADR-0009_wiki-browsing-404-hides-existence.md"
  - "../adr/IADR-0030_operator-role-and-config-viewer-policy.md"
  - "../adr/IADR-0035_frontend-role-based-nav-and-existence-hiding.md"
  - "../adr/IADR-0036_sc11-config-viewer-visualization.md"
---

# テスト仕様書: SC-11 構成ビューア

> SC-11 の画面テスト観点を写像する。#137（グラフ）・#138（ドリフト）・#140（アクセス制御）を通貫。

## 起点となる計画書（トレーサビリティ）

> **［2026-08-04 / #490］ルートパスを計画へ是正した。** SPA のルータを TanStack Router へ差し替えるにあたり、本書内のルート表記を [05_screens §共通シェル](../../planning/projects/microservices-platform/05_screens/01_screens.md)「ルートパス（wireframe の URL バー準拠）」の値へ揃えた（[[IADR-0124]] 決定 6）。テスト観点そのものは変えていない。


- 機能要求（FR）: FR-15（構成の可視化・ドリフト検出・管理者/運用者限定）
- 画面（SC）: SC-11 構成ビューア
- 受け入れ基準の所在: Issue #137 / #138 / #140（親 #122）／ SC-11 仕様書 テスト観点

## テスト対象・範囲

- 対象: `features/sc11-config`（実効構成表示・ドリフト表示・アクセス制御）と関連基盤（`RequireRole`・ロール別 nav）。
- 対象外: `/bff/admin/config`・`/bff/admin/config/drift` のサーバ側テスト（既存・#118 E2E で実測済）。

## テスト観点

- 実効構成（#137）: 構成バージョン・段（無効段グレーアウト・終端）・イベント接続・ポート・コネクタの表示。
- ドリフト（#138）: 種別・深刻度・対象・説明の一覧・強調、0件時「OK」明示、取得失敗時のドリフト領域のみ縮退。
- 構成バージョン履歴（#139, IADR-0046）: コミット ID（短縮）・適用日時・適用者・その時点のドリフト有無を新しい順で一覧、
  0件時「適用履歴はありません。」、取得失敗時の履歴領域のみ縮退。データ源・縮退（注入履歴／現在バージョン単一／空）は
  API 側（`ConfigInspectionService` 単体テスト）で検証する。
- アクセス制御（#140, 存在秘匿）: 管理者・運用者のみアクセス可、権限外は直接遷移で NotFound（存在を示さない）・メニュー非表示・構成 API を呼ばない。
- 秘匿/異常系: 404（秘匿）中立表示、5xx alert、loading。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-01 | ConfigViewer・config=200 | `/admin/config-viewer` 表示 | バージョン・段・接続・ポート・コネクタ表示 | #137 グラフ・一覧 | 自動(単体) |
| T-02 | config に無効段 | 表示 | 無効段グレーアウト・終端表示 | #137 | 自動(単体) |
| T-03 | drift=0件 | 表示 | 「ドリフトなし（OK）」＋確認時刻 | #138 0件OK | 自動(単体) |
| T-04 | drift=1件(StaleStage/high) | 表示 | 種別・深刻度・対象・説明の一覧＋強調、バッジ「ドリフト N 件」 | #138 一覧・強調 | 自動(単体) |
| T-05 | drift 取得失敗 | 表示 | ドリフト領域のみ縮退、構成は表示継続 | #138 頑健性 | 自動(単体) |
| T-06 | `platform-admin` | `/admin/config-viewer` ルート要素描画 | 画面表示（許可） | #140 管理者許可 | 自動(単体) |
| T-07 | `platform-operator` | `/admin/config-viewer` ルート要素描画 | 画面表示（許可） | #140 運用者許可 | 自動(単体) |
| T-08 | ロールなし利用者 | `/admin/config-viewer` 直接遷移 | NotFound（存在秘匿）・構成 API 未呼出 | #140 存在秘匿 | 自動(単体) |
| T-09 | ロールなし利用者 | ナビ描画 | 「構成ビューア」非表示 | #140 メニュー非表示 | 自動(単体) |
| T-10 | `platform-operator` | ナビ描画 | 「構成ビューア」表示（運用ダッシュボードは非表示） | #140 | 自動(単体) |
| T-11 | config=404 | 表示 | 中立「構成情報は利用できません。」 | 存在秘匿 | 自動(単体) |
| T-12 | config=5xx | 表示 | `role="alert"` 取得失敗 | 異常系 | 自動(単体) |
| T-13 | 未認証 | `/admin/config-viewer` を開く | `/login` へ誘導 | ルート登録・認証ガード | 自動(E2E) |
| T-14 | history=2件 | 表示 | 短縮コミット・適用日時・適用者・ドリフト有無（あり/なし）を新しい順で一覧 | #139 履歴表示 | 自動(単体) |
| T-15 | history=0件 | 表示 | 「適用履歴はありません。」 | #139 空表示 | 自動(単体) |
| T-16 | history 取得失敗 | 表示 | 履歴領域のみ縮退、構成は表示継続 | #139 頑健性 | 自動(単体) |
| T-17 | 注入履歴あり/なし/空（API） | `GetVersionHistoryAsync` | 注入は新しい順で surfacing／未注入は現在バージョン単一へ縮退／全て空は空一覧 | #139 データ源・縮退（IADR-0046） | 自動(単体) |
| T-18 | 無認証で `/history` | GET | 404 秘匿＋監査 `config.history.read=denied`／許可は `granted` | #139 存在秘匿・監査 | 自動(単体) |

## テストデータ

- ロール別ダミー `User`（access_token に `realm_access.roles`）。
- `EffectiveConfig` ダミー（無効段含む）、`DriftReport` ダミー（0件／StaleStage 1件）、
  `ConfigVersionEntry` ダミー（新しい順 2 件・`hadDrift` あり/なし）。

## 関連仕様

- 画面仕様書: `docs/screens/SC-11_configuration-viewer.md`
- 作業仕様書: `docs/specs/20260708_issue-137_sc11-config-graph.md` / `..._issue-138_sc11-drift.md` / `..._issue-140_sc11-access-control.md`
- 実装 ADR: [[IADR-0035]]（ロール別 nav・存在秘匿）、[[IADR-0036]]（可視化方式）

## 未決事項

- なし
