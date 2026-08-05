---
title: SC-09 管理者設定（ABAC） テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-09
  - UC-05
  - FR-05
  - FR-09
  - IADR-0009
  - IADR-0040
  - IADR-0129
author: claude
created: 2026-07-09
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - "../screens/SC-09_admin-abac-settings.md"
  - "../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md"
  - "../adr/IADR-0040_admin-abac-bff-passthrough-and-admin-only.md"
  - "../specs/20260805_issue-504_sc09-11-admin-ops-screens.md"
---

# テスト仕様書: 管理者設定（ABAC）（SC-09）

> **［2026-08-05 / #504］新スタックでの再実装に合わせて画面側を全面改訂した。**
> **§バックエンド（BFF・xUnit）と §foundation は落とさずに残す**——当該テストは実在し続けており
> （`BffAuthzEndpointTests` / `apiClient.test.ts`）、**画面の権限は片側だけを固定しても実効境界にならない**
> （#503 が SC-05〜07 でバックエンドの節を落とし、#510 として起票された先例がある）。

対象（画面）: `src/knowledge/frontend/src/features/sc09-admin-abac/`
テスト: `abacVocabulary.test.ts`（純関数）／ `AdminAbacSettingsPage.test.tsx`（Vitest + Testing Library。
画面 ＋ **アクセス制御**）／ E2E は `src/platform/frontend/e2e/sc09-admin-abac.smoke.spec.ts`

対象（API）: `src/platform/backend/Bff/Platform.Bff.Tests/BffAuthzEndpointTests.cs`

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-09 ／ ユースケース（UC）: **UC-05**（ABAC 権限を管理する）／
  機能要求（FR）: **FR-09**（ABAC 属性・ポリシー管理）・**FR-05**（ABAC）
- 受け入れ基準の所在: issue #504 §受け入れ基準 ／ 作業仕様書
  [20260805_issue-504](../specs/20260805_issue-504_sc09-11-admin-ops-screens.md) §受け入れ基準

## UC-05 のフロー → テストの写像

| UC-05 のフロー | 画面での現れ方 | テスト |
| --- | --- | --- |
| 基本 1. 利用者属性・文書属性を定義する | 属性体系タブ（一覧 ＋ 追加 ＋ 削除） | `lists the attribute dictionary on the attribute tab` ／ `creates an attribute with the parsed allowed values` |
| 基本 2. ポリシー（利用者属性 × 文書属性 → 許可アクション）を定義する | ポリシー定義タブ（一覧 ＋ 構造化条件エディタ ＋ 保存） | `opens on the policy tab and lists the policies with their conditions` ／ `builds a policy condition from the defined attributes only` |
| 基本 3. 保存前に矛盾を検証する | 検証結果パネル | `shows the server-side contradiction detail in the validation panel` |
| 基本 4. 保存すると認可判定へ即時反映される | 検証結果パネルの完了表示 | `confirms in the validation panel that a saved policy takes effect immediately` |
| **例外**. 参照中の属性は削除できない | 409 の理由表示 ＋ 参照元ポリシー名 | `explains a 409 when deleting a referenced attribute and keeps the server detail` |
| 認可判定の実行（`AbacEvaluator`） | **写像しない**（サーバ側の責務） | — |

## 計画の要素 → 実装／テストの対応

| 計画 §SC-09 の要素 | テスト |
| --- | --- |
| 属性体系エディタ | 上表 基本 1 |
| **タグ辞書** | **実装しない**（契約の不在）。`does not render the tag dictionary or a dry-run validate button` |
| **辺の型辞書** | **実装しない**（着手保留・[[IADR-0119]]）。`does not render the edge-type dictionary` |
| ポリシー定義 | 上表 基本 2 |
| 検証結果 | 上表 基本 3・4 |
| 入力表「対象属性｜選択｜**定義済み属性のみ**」 | `builds a policy condition from the defined attributes only`（選択肢が属性辞書に由来する） |
| 入力表「ポリシー条件｜**条件式**」 | **部分**（集合所属のみ）。純関数 P5〜P8 が表現の範囲を固定する |

## テストケース（画面）

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | 既定タブ | hi-fi 417（ポリシー定義が選択中） | `Tabs` の既定が「ポリシー定義」であり、条件・状態・アクションが出る |
| 2 | 条件の要約 | 契約 | `利用者 dept = 経理` の形。**条件なしは「条件なし（すべてに一致）」**と明示する（空欄にしない） |
| 3 | 状態表示 | INDEX 決定 21 | 有効／無効を `StatusBadge`（色 ＋ アイコン ＋ テキスト）で示す |
| 4 | 属性辞書 | FR-09 | タブ切替で一覧が出る。スコープは `Tag`（分類名） |
| 5 | 属性追加 | FR-09 | 許可値のカンマ区切りを配列へ。ペイロードを固定する |
| 6 | **参照中削除の 409** | [[IADR-0006]] / [[IADR-0040]] | 理由（固定文言）と**サーバが返す参照元ポリシー名**（`ApiError.details`）の両方を出し、**tone に合わせてラベルも「注意」**にする（琥珀に「エラー」と書かない）。fixture は**実サーバ応答の形**（`details` 非空）を再現する |
| 7 | **条件エディタ** | 計画の入力表 | 選択肢は属性辞書由来。積んだ条件がチップで見え、保存のペイロードへ入る |
| 8 | **保存前検証（400）** | 計画 §アクション | サーバの矛盾検証の詳細を**検証結果パネル**に `role="alert"` で出す |
| 9 | 保存成功 | 計画 §アクション | 「認可判定へ即時反映されます」を `role="status"` で出す |
| 10 | 必須未入力 | 計画の入力表 | 名前が空のあいだ保存ボタンは `disabled` |
| 11 | **再取得** | [[IADR-0127]] 決定 5 | 切替の成功後に一覧を取り直す（`invalidateQueries` のみ） |
| 12 | **操作を跨いだ状態** | [[IADR-0127]] 決定 7 | **異なるミューテーション**を続けたとき、古い失敗バナーが残らない（属性: 削除 → 追加／ポリシー: 保存 → 切替） |
| 13 | **異常系（縮退しない）** | — | 取得失敗を `role="alert"` で出し、「0 件」へ寄せない |
| 14 | **着手保留**（実装しない要素） | [[IADR-0119]] | 「辺の型」が無い。**先に 2 つの区画が在ることを確かめてから**無いことを見る |
| 15 | **契約の不在**（実装しない要素） | 画面仕様書 §hi-fi 対応 #2・#11 | 「タグ辞書」タブと「検証」ボタンが無い |
| 16 | **他 issue の射程** | 同 #5 | MCP クライアント管理へのリンクが無い（遷移先が未実装） |
| 17 | ロケール `en` | ADR-0031 | 見出しが英語で描画される |

## アクセス制御・存在秘匿（画面）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| A1 | 許可 | `platform-admin` は開ける |
| A2 | **存在秘匿** | `platform-operator` と一般利用者は **`NotFound`**（画面の見出しが出ない）。**BFF を呼ばない** |
| A3 | **markup 一致** | 権限による秘匿の描画が `foundation/ui/NotFound`（＝不在）と**同じ markup** である（#490 の作法） |
| A4 | ナビ | `requiresAnyRole: [platform-admin]`・`group: 'admin'` |

## 純関数（`abacVocabulary.test.ts`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| P1 | 値集合 | アクションが契約の **3 値**（`read` / `analyze` / `manage`）と完全一致する |
| P2 | 値集合 | スコープが契約の **2 値**（`document` / `user`）と完全一致する |
| P3 | 写像 | 各値に表示名が対で決まる |
| P4 | **未知の値** | 生値をそのまま出す（`—`・「不明」へ丸めない） |
| P5 | 条件の振り分け | スコープに従って利用者条件・文書条件へ分かれる |
| P6 | 条件の集約 | 同じ属性の複数値が 1 つの集合へまとまる |
| P7 | 重複 | 同じ組を 2 度積んでも値が重複しない |
| P8 | 要約の順序 | 一覧の条件は**利用者属性が先**（計画の並び） |
| P9 | 許可値の解釈 | カンマ区切りを配列へ。空要素を落とす |

## バックエンド（BFF・xUnit）

対象: `src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/AuthzBffEndpoints.cs`
テスト: `src/platform/backend/Bff/Platform.Bff.Tests/BffAuthzEndpointTests.cs`

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | ポリシー一覧 | FR-09 | admin で一覧が返る | `ListPolicies_AsAdmin_ReturnsPolicies` |
| 2 | 属性一覧 | FR-09 | admin で属性辞書が返る | `ListAttributes_AsAdmin_ReturnsAttributes` |
| 3 | ロール制限 | [[IADR-0040]] | **operator も 403**（admin 専用） | `ListPolicies_AsNonAdmin_IsForbidden` |
| 4 | 無認証 | [[IADR-0040]] | 匿名は 401 | `ListPolicies_WhenAnonymous_IsUnauthorized` |
| 5 | ポリシー登録 | FR-09 | 201 で登録 | `CreatePolicy_AsAdmin_Returns201` |
| 6 | 検証透過 | FR-09 / [[IADR-0040]] | 保存前検証 400 を透過 | `CreatePolicy_WhenValidationFails_Passes400Through` |
| 7 | 属性登録 | FR-09 | 201 で登録 | `CreateAttribute_AsAdmin_Returns201` |
| 8 | 競合透過 | [[IADR-0006]] | 参照中削除 409 を透過 | `DeleteAttribute_WhenReferenced_Passes409Through` |
| 9 | 有効切替 | FR-09 | PATCH で有効／無効切替 | `SetPolicyActive_AsAdmin_Succeeds` |
| 10 | 後段不達 | [[IADR-0040]] | 後段ダウン時に 502 へ縮退（例外フロー・レビュー #170） | `ListPolicies_WhenBackendUnreachable_Returns502` |

## foundation（Vitest）

対象: `src/platform/frontend/src/foundation/api/apiClient.ts` / `ApiError.ts`
テスト: `src/platform/frontend/src/foundation/api/apiClient.test.ts`

| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| 1 | 検証詳細抽出 | 400 → `validation`・`details` にメッセージ抽出 | `maps 400 to validation and extracts ValidationProblem detail messages` |
| 2 | 競合詳細抽出 | 409 → `conflict`・`details` に detail | `maps 409 to conflict and extracts the problem detail` |

## E2E（Playwright）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| E1 | ルートの実在 ＋ 認証ガード | 未認証で `/admin/abac` を開くと `/login` へ誘導される（ルート未登録なら `NotFound` が出て `/login` へ行かないため、この 1 本でルートの実在も固定できる） |

## ロール・存在秘匿の担保

- **画面と API の両側で同じ境界を固定する。** 画面側は A1〜A4、API 側は BFF の 3 / 4。
  **画面のテストだけでは穴は塞げない**——UI 制御はサーバ側の実効境界の写しであり、
  API を直接叩く経路はテストできないためである。
- BFF・後段（AuthorizationService）とも `AdminOnly`。**operator も 403** である（[[IADR-0040]]）。

## 実行

- `pnpm run test -- knowledge/frontend/src/features/sc09-admin-abac`（純関数 **9** ＋ 画面 **17** ＋ アクセス **4** ケース）
- `pnpm run test:coverage`（カバレッジ・ラチェット維持）
- `dotnet test src/platform/backend/Bff/Platform.Bff.Tests --filter BffAuthzEndpointTests`
- `pnpm --filter @platform/frontend run test:e2e`

## 未決事項

- 契約の不在 3 件（タグ辞書・dry-run 検証・条件式の表現力）は
  `feedback/20260805_sc09-11-admin-ops-contract-gaps.md`。裁定までテストも書かない。
