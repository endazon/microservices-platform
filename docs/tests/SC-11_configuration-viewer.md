---
title: SC-11 構成ビューア テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-11
  - FR-15
  - IADR-0009
  - IADR-0029
  - IADR-0030
  - IADR-0036
  - IADR-0046
  - IADR-0129
author: claude
created: 2026-07-08
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/06_technical/05_observability-ops.md"
related_specs:
  - "../screens/SC-11_configuration-viewer.md"
  - "../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md"
  - "../adr/IADR-0009_wiki-browsing-404-hides-existence.md"
  - "../adr/IADR-0029_config-info-api-placement-and-drift-granularity.md"
  - "../adr/IADR-0030_operator-role-and-config-viewer-policy.md"
  - "../adr/IADR-0035_frontend-role-based-nav-and-existence-hiding.md"
  - "../adr/IADR-0036_sc11-config-viewer-visualization.md"
  - "../adr/IADR-0046_config-version-history-source.md"
  - "../specs/20260805_issue-504_sc09-11-admin-ops-screens.md"
---

# テスト仕様書: SC-11 構成ビューア

> **［2026-08-05 / #504］新スタックでの再実装に合わせて画面側を全面改訂した。**
> **§API 側（T-17 / T-18 相当）の観点は落とさずに残す**——当該テストは実在し続けており
> （`ConfigInspectionService` の履歴縮退・`ConfigBffEndpoints` の 404 秘匿と監査）、
> **存在秘匿は片側だけを固定しても実効境界にならない**（#503 が SC-05〜07 でバックエンドの節を
> 落とし、#510 として起票された先例がある）。**改訂前後で節の構成を突き合わせた**うえで、
> 旧 T-01〜T-16 は画面側の表へ、旧 T-17 / T-18 は §API 側 の表へ写した。

対象（画面）: `src/knowledge/frontend/src/features/sc11-config/`
テスト: `driftView.test.ts`（純関数）／ `ConfigViewerPage.test.tsx`（Vitest + Testing Library）／
`access.test.tsx`（**#140 の観点を引き継ぐアクセス制御**）／
導線は `src/knowledge/frontend/src/features/opsFlow.test.tsx`／
E2E は `src/platform/frontend/e2e/sc11-config.smoke.spec.ts`

対象（API）: `src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/ConfigBffEndpoints.cs` ／
`Platform.Shared.Infrastructure/Foundation/Introspection/`（`ConfigInspectionService` / `DriftDetector`）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-11 ／ 機能要求（FR）: **FR-15**（構成の可視化・ドリフト検出・管理者/運用者限定）／
  ユースケース（UC）: **—（運用・保守要求）**
- 受け入れ基準の所在: issue #504 §受け入れ基準 ／ 作業仕様書
  [20260805_issue-504](../specs/20260805_issue-504_sc09-11-admin-ops-screens.md) §受け入れ基準／
  旧 issue #137（グラフ）・#138（ドリフト）・#139（履歴）・#140（アクセス制御）

## 旧テストケース（T-01〜T-18）との対応

**改訂で観点を落としていないことを突き合わせた表である。**

| 旧 ID | 旧観点 | 改訂後 |
| --- | --- | --- |
| T-01 | 実効構成の表示 | 画面 1 |
| T-02 | 無効段のグレーアウト・終端 | 画面 2（**淡色だけに頼らず `StatusBadge` を足した**） |
| T-03 | ドリフト 0 件の「OK」＋確認時刻 | 画面 4 |
| T-04 | ドリフト一覧・強調・バッジ | 画面 3 |
| T-05 | ドリフト取得失敗の領域縮退 | 画面 7（5xx）・画面 8（404） |
| T-06 | `platform-admin` 許可 | アクセス A1 |
| T-07 | `platform-operator` 許可 | アクセス A2 |
| T-08 | 権限外は NotFound・API 未呼出 | アクセス A3（**markup 一致の A4 を追加**） |
| T-09 | ナビ非表示（権限外） | `Layout.test.tsx`（共通シェル側）＋ アクセス A5（ナビの宣言） |
| T-10 | ナビ表示（運用者） | 同上 |
| T-11 | 404 の中立表示 | 画面 11 |
| T-12 | 5xx の `role="alert"` | 画面 12 |
| T-13 | 未認証は `/login` | E2E E1 |
| T-14 | 履歴 2 件の表示 | 画面 5 |
| T-15 | 履歴 0 件 | 画面 6 |
| T-16 | 履歴取得失敗の領域縮退 | 画面 9（5xx）・画面 10（404） |
| **T-17** | **`GetVersionHistoryAsync` の注入・縮退** | **§API 側 1**（据え置き） |
| **T-18** | **`/history` の 404 秘匿 ＋ 監査** | **§API 側 2**（据え置き） |

## テストケース（画面）

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | 実効構成 | FR-15 / 計画 §主要素 | 構成バージョン（コミット短縮 7 桁・適用日時・適用者）＋ 段・イベント接続・ポート・コネクタ。3 本の API を呼ぶ |
| 2 | 段の表現 | [[IADR-0036]] / INDEX 決定 21 | 終端は「（終端）」。**無効段は淡色 ＋ `StatusBadge`「無効」**（色だけに頼らない） |
| 3 | ドリフト | #138 | 種別（表示名）・深刻度（`StatusBadge`）・対象・説明。ヘッダのバッジ「ドリフト N 件」。**該当段から明細へのページ内リンク** |
| 4 | ドリフト 0 件 | #138 | 「ドリフトなし（OK）。」＋**確認時刻**（未検出と未実行を区別する） |
| 5 | 履歴 | #139 / [[IADR-0046]] | API が返した順（新しい順）で一覧。`hadDrift` は **3 値**（あり／なし／—） |
| 6 | 履歴 0 件 | #139 | 「適用履歴はありません。」（節ごと消さない） |
| 7 | **ドリフトの縮退（5xx）** | [[IADR-0129]] 決定 5・決定 3 | ドリフトだけ落ちても構成は出し続ける。**5xx は `role="alert"` の障害として出す**（中立文言へ寄せない）。**ヘッダのバッジは出さない**（「0 件」と紛れる） |
| 8 | **ドリフトの縮退（404）** | [[IADR-0129]] 決定 3 | 404 は中立文言「ドリフト情報は利用できません。」（`role="alert"` を出さない） |
| 9 | **履歴の縮退（5xx）** | 同上 | 履歴だけ落ちても構成は出し続ける。5xx は `role="alert"` |
| 10 | **履歴の縮退（404）** | 同上 | 404 は中立文言「バージョン履歴は利用できません。」 |
| 11 | **404 の中立化** | [[IADR-0009]] | 「構成情報は利用できません。」（`role="alert"` を出さない） |
| 12 | **5xx は中立化しない** | 同上 | `role="alert"` で障害として出す |
| 13 | **実効構成が無ければ他も出さない** | [[IADR-0129]] 決定 5 | 構成が取れないとき、ドリフト・履歴の領域を描かず、**ヘッダのドリフトバッジも出さない**（何に対する差分か読めないため）。**404〔秘匿〕と 5xx〔障害〕の両方**で見る（**観点 13 だけはテスト 2 本**） |
| 14 | **参照専用** | 計画 §入力 | 画面上のボタンは**再取得の 1 つだけ**。**先に再取得ボタンと注記が在ることを確かめてから**構成変更の操作が無いことを見る |
| 15 | 再取得 | — | 3 本を取り直す（`invalidateQueries` のみ。手書きの再取得を持たない） |
| 16 | ロケール `en` | ADR-0031 | 見出しが英語で描画される |

## アクセス制御・存在秘匿（`access.test.tsx`。**#140 の観点を引き継ぐ**）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| A1 | 許可 | `platform-admin` は開ける |
| A2 | 許可 | `platform-operator` も開ける（`ConfigViewer`。[[IADR-0030]]） |
| A3 | **存在秘匿** | ロールを持たない利用者は **`NotFound`**。**構成 API を呼ばない** |
| A4 | **markup 一致**（#504 で追加） | 権限による秘匿の描画が `foundation/ui/NotFound`（＝不在）と**同じ markup**（#490 の作法） |
| A5 | ナビ | `requiresAnyRole: [platform-admin, platform-operator]`・`group: 'ops'` |

**アプリ全体（共通シェル込み）での markup 一致は `Layout.test.tsx` が見ている**
（「未知パスの `NotFound`」と「`/admin/config-viewer` の `NotFound`」が同じ描画になること）。

## 純関数（`driftView.test.ts`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| P1 | 値集合 | ドリフト種別が契約の **5 値**と完全一致する |
| P2 | 値集合 | 深刻度が契約の **2 値**と完全一致する |
| P3 | 写像 | 5 種別に表示名が対で決まる |
| P4 | **未知の種別** | 生値をそのまま出す（丸めない） |
| P5 | 写像 | 深刻度が文言 ＋ tone の対で決まる（INDEX 決定 21） |
| P6 | **未知の深刻度** | 生値 ＋ 中立 tone |
| P7 | 強調対象 | `finding.target` の集合を作る（段名のみ） |
| P8 | **`hadDrift` の 3 値** | `null` を `false` へ丸めない（「ドリフトが無かった」と誤読させない） |

## 導線（`opsFlow.test.tsx`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| A | SC-10 → SC-11 | 運用ダッシュボードから遷移して構成バージョンが出る |
| B | 運用者の到達 | 運用者は SC-11 へ直接到達できる（SC-10 は `NotFound`） |

## API 側（xUnit。**旧 T-17 / T-18 の据え置き**）

対象: `ConfigBffEndpoints.cs` ／ `ConfigInspectionService` ／ `DriftDetector`

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | **履歴のデータ源・縮退** | #139 / [[IADR-0046]] | 注入履歴は新しい順で surfacing ／ 未注入は現在バージョン単一へ縮退 ／ すべて空なら空一覧（`GetVersionHistoryAsync`） |
| 2 | **404 秘匿 ＋ 監査** | [[IADR-0029]] / [[IADR-0009]] | 無認証・非権限は 404 で秘匿し監査へ `denied`、権限ありは `granted` を記録する（`config.read` / `config.drift.read` / `config.history.read`） |
| 3 | ドリフト判定 | [[IADR-0029]] | 種別 5 値の検出（`DriftDetector`）。判定は API 側が行い、画面は結果を表示するのみ |

> **サーバ側は `RequireAuthorization` を付けずにハンドラ内で認可を判定する**——付けると無認証が
> 404 到達前に **401 で短絡して存在が漏れる**。この作法自体が #2 の検証対象である。

## E2E（Playwright）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| E1 | ルートの実在 ＋ 認証ガード | 未認証で `/admin/config-viewer` を開くと `/login` へ誘導される |

## テストデータ

- ロール別のダミー `User`（`renderUnitRoute` が `realm_access.roles` を持つ JWT を生成する）。
- `EffectiveConfig` ダミー（**無効段・終端段を含む**）、`DriftReport` ダミー（0 件／`BindingMismatch` 1 件）、
  `ConfigVersionEntry` ダミー（新しい順 2 件・`hadDrift` あり／なし）。
- 3 本の API は**パスで応答を振り分け、1 本だけ壊せる**モックにする（領域ごとの縮退を試験するため）。

## 実行

- `pnpm run test -- knowledge/frontend/src/features/sc11-config`

  **件数は母集合を明記する**（観点行・`it` 宣言・vitest が数える `Tests` は一致しない）。
  下表は上記コマンドの出力と突き合わせた実測である。

  | 括り | 本書の観点行 | `it` 宣言 | vitest の `Tests` |
  | --- | --- | --- | --- |
  | 純関数（`driftView.test.ts`） | **8**（P1〜P8） | **8**（うち `it.each` 2） | **13** |
  | 画面（`ConfigViewerPage.test.tsx`） | **16** | **17** | **17** |
  | アクセス（`access.test.tsx`） | **5**（A1〜A5） | **5** | **5** |
  | **合計** | **29** | **30** | **35**（3 ファイル） |

  観点行と `it` 宣言がずれるのは、**画面 観点 13 が 2 本に分かれる**（実効構成の 404〔秘匿〕と
  5xx〔障害〕の両方でヘッダのバッジが消えることを見る）ためである。
  `it` 宣言と `Tests` がずれるのは `it.each` の展開による。
- `pnpm run test -- knowledge/frontend/src/features/opsFlow.test.tsx`（導線）
- `pnpm run test:coverage`（カバレッジ・ラチェット維持）
- `dotnet test src/platform/backend/Bff/Platform.Bff.Tests --filter Config`
- `pnpm --filter @platform/frontend run test:e2e`

## 未決事項

- なし（**ドリフト判定の粒度**は [[IADR-0029]] の既定を据え置く。
  `docs/api/openapi.yaml` への `/bff/admin/config` 群の追加は #506 の射程）。
