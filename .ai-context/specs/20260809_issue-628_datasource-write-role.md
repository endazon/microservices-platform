---
title: SC-06 データソースの登録・無効化を管理者限定へ狭め、手動同期の分類を計画の裁定へ揃える
type: spec
status: done
related_ids: [FR-01, FR-02, UC-04, SC-06, IADR-0039, IADR-0044, IADR-0127, IADR-0128]
author: Claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
related_specs:
  - "../adr/IADR-0039_datasource-management-bff-and-role-gating.md"
  - "../adr/IADR-0044_backend-service-authorization-defense-in-depth.md"
  - "../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md"
  - "../../docs/screens/SC-06_datasource-management.md"
  - "../../docs/tests/SC-06_datasource-management.md"
  - "../../docs/api/BFF_bff-surface.md"
  - ./20260805_issue-501_retry-admin-only.md
  - ./20260808_issue-534-537_datasource-contract-bundle.md
---

# 仕様書: データソースの登録・無効化を管理者限定へ狭める（#628）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-01**（データソースカタログ）／**FR-02**（取り込み）
- ユースケース（UC）: **UC-04**（データソース同期。代替フロー「手動同期を実行する」）
- 画面（SC）: **SC-06 データソース管理画面**
  （05_screens/01_screens.md（計画リポ）
  §SC-05「管理系 3 画面の閲覧ロール」`:282`〜`:285`・§SC-06 §アクセス制御 `:294`）
- 関連 ADR:
  [IADR-0039](../adr/IADR-0039_datasource-management-bff-and-role-gating.md)（BFF ゲートとロール）／
  [IADR-0044](../adr/IADR-0044_backend-service-authorization-defense-in-depth.md)（後段の多層防御）／
  [IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md)
  決定 1（`AdminOnly` をグループ既定へ**積む**形。本作業はこの形をそのまま適用する）／
  [IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md)
  決定 1（画面は押しても結果が変わらないボタンを置かない・**理由を書いて消す**）／
  [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4（PR ではなく issue を分割する）
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
- 本リポジトリの起点: **#628**（発見元 #534 / PR #627・親 #454）
- 計画側の裁定: **planning#299**（クローズ済み・PR planning#300 で着地）

## 計画 pin の前進と ID レンジの引き直し

本作業で planning submodule の pin を **`90f5251` → `31a69c9`** へ進める（planning#300 の着地を取り込むため。
**本作業の根拠そのものが新 pin にしか無い**）。`.claude/rules/traceability.md` の手順に従い、
pin を進めたので 4 種すべてのレンジを引き直した。**いずれも動いていない。不変であることも実測の結果である。**

```console
$ cd planning && git rev-parse --short HEAD
31a69c9
$ grep -oE 'FR-[0-9]+' projects/microservices-platform/02_requirements/01_requirements.md | sort -u | wc -l
22                    # FR-01..22（欠番なし）
$ grep -oE 'UC-[0-9]+' projects/microservices-platform/03_usecases/01_usecases.md | sort -u | tr '\n' ' '
UC-01 … UC-11         # 11 件・欠番なし
$ grep -oE 'SC-[0-9]+' projects/microservices-platform/05_screens/01_screens.md | sort -u | tr '\n' ' '
SC-01 … SC-21         # 21 件・欠番なし
$ ls projects/microservices-platform/07_adr/ADR-*.md | wc -l
45                    # ADR-0001..0045・欠番なし
$ for f in .../ADR-*.md; do grep -m1 '^status:' "$f"; done | grep -c Proposed
6                     # ADR-0023 / 0038 / 0039 / 0040 / 0041 / 0042（前回と同一）
```

したがって `.claude/rules/traceability.md` の**条文は書き換えない**（走査基準の pin 表記のみ追随させる）。

## 目的・背景

計画 §SC-06 §アクセス制御（確定・2026-08-05）は次を定める。

> **閲覧は管理者・運用者。登録・更新・無効化は管理者限定**

**実装はこれに反していた。** #534（PR #627）の実装中に実測で見つけたが、#534 の射程外なので
別 issue（#628）へ切り出していた（[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4）。本作業がその是正である。

あわせて #628 が挙げていた**手動同期の扱い**は、実装側で決めてよい種類の判断ではないため
`/plan-feedback` で計画へ環流し（feedback/20260809_sc06-manual-sync-role-classification.md（環流記録。計画リポ `projects/microservices-platform/10_feedback/20260809_sc06-manual-sync-role-classification.md` へ移設））、
**planning#299 で裁定を得た**。結論は **「手動同期は破壊的操作に含めない。運用者にも開く」**であり、
**現行実装（admin ＋ operator）を追認する**。したがって**手動同期のコードは変更しない**。

## 母集合（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）

「計画の**破壊的操作は管理者限定**が効く口」を、**誤りの側（＝広いまま残っている口）から**引いた。
**拡張子で絞らず、パスから引いた**（追跡下の全ファイル。`planning/` と `src/ai-stock-trading` は除く）。

```console
$ git grep -ln "RequireAuthorization\|RequireRole" -- . ':!planning' ':!src/ai-stock-trading' | wc -l
95                       # 軸 1: 認可宣言を持つ全ファイル
$ for f in <BFF 6 本 + Services 8 本>; do grep -n "MapGroup\|Map(Get|Post|Put|Patch|Delete)\|RequireAuthorization" $f; done
```

### 軸 1: 実装の口（グループ既定と個別の上書きを突き合わせる）

| 画面 | 口 | 計画 | 実装（`a7b89e4` 時点） | 判定 |
| --- | --- | --- | --- | --- |
| SC-06 | `GET /datasources`（一覧・個別） | 管理者・運用者 | admin ＋ operator | ✅ |
| SC-06 | **`POST /datasources`** | **管理者限定** | admin ＋ operator | ❌ **本作業で是正** |
| SC-06 | **`DELETE /datasources/{id}`** | **管理者限定** | admin ＋ operator | ❌ **本作業で是正** |
| SC-06 | `PUT` / `PATCH /datasources/{id}` | 管理者限定 | **admin のみ** | ✅（#534 が計画どおり作った） |
| SC-06 | `POST /datasources/{id}/sync` | **破壊的操作に含めない**（planning#299） | admin ＋ operator | ✅ **追認・変更しない** |
| SC-07 | `POST /bff/conversion/jobs/{id}/retry` | 管理者限定（再変換） | **admin のみ** | ✅（#501 が是正済み・有効） |
| SC-05 | `POST` / `PUT` / `PATCH` / `DELETE` / `publish` / `archive` `/documents` | **管理者限定**（文書の編集/削除） | admin ＋ operator | ❌ **同型の逸脱。射程外**（下記） |

**BFF 側（`/bff/*`）も同じ表で突き合わせ、後段と同じ結論になることを確認した**
（`DataSourceBffEndpoints.cs` / `DocumentBffEndpoints.cs` のグループ既定はいずれも admin ＋ operator）。

### 軸 2: 認可を記述する文書（同じ事実の写しがどこにあるか）

- [`docs/api/BFF_bff-surface.md`](../../docs/api/BFF_bff-surface.md) `:124`〜`:130`（`/bff/datasources` の認可列 7 行）
- [`docs/screens/SC-06_datasource-management.md`](../../docs/screens/SC-06_datasource-management.md)
  §アクセス（`:62`）・§未決事項 5（`:253`〜`:257`。#534 が追記した）
- [`docs/tests/SC-06_datasource-management.md`](../../docs/tests/SC-06_datasource-management.md)（テスト仕様の認可ケース）
- `docs/api/openapi.yaml`（`/bff/datasources` の各操作。**認可はスキーマに載らない**ので `description` の記述のみ）

### 軸 3: テスト（現行の期待値がどちらを固定しているか）

- `DataSourceAuthorizationTests.cs`（サービス直）／`BffDataSourceEndpointTests.cs`（BFF 経由）
- `DataSourceManagementPage.test.tsx`（画面の出し分け）

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| **SC-05 文書の書き込み口（7 口）** | **同型の逸脱を実測で見つけたが、#628 の射程外である。**[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4 に従い**別 issue へ切り出した**（**#629**。PR は分割しない）。#534 → #628 と同じ扱いである。**黙って直さない・黙って見送らない**。`publish` / `archive` は planning#299 が新設した「実行系だが破壊的ではない」の基準で判断が要る点も #629 へ書いた |
| `POST /datasources/{id}/sync` | planning#299 が**現行実装を追認**した。変更すると裁定に反する |
| SC-09 / SC-10 / SC-11 の口 | 計画の「管理系 3 画面（SC-05/06/07）」の括りの外であり、別の裁定（Q28 等）に従う。本作業の基準は適用しない |
| `src/ai-stock-trading` | 別プロジェクトの submodule（本リポから変更しない） |
| `planning/` | pin 更新のみ。計画書そのものを本リポから書き換えない |

## 実装方針

### 1. 後段（DataSourceService）

`POST /` と `DELETE /{id}` に `.RequireAuthorization(PlatformAuthPolicies.AdminOnly)` を積む。
**グループ既定（admin ＋ operator）は残す** —— [IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) 決定 1 の形であり、AND 合成で実効 admin のみになる。
グループを絞らないのは、**閲覧ロールの裁定（Q19）が「閲覧は運用者へ開く」であり、
グループ既定はその閲覧の下限を表しているから**である。

### 2. BFF（`DataSourceBffEndpoints.cs`）

同じ 2 口へ同じポリシーを積む（[IADR-0044](../adr/IADR-0044_backend-service-authorization-defense-in-depth.md) の多層防御）。**片側だけだと BFF 迂回で通る／画面だけ 403 になる。**

### 3. 画面（`DataSourceManagementPage.tsx`）

`useHasAnyRole(PlatformRole.Admin)` で `canWrite` を導き、**「＋ ソース登録」と行操作「無効化」を
運用者へ出さない**。**手動同期は出したままにする**（planning#299）。
**無言で消さない** —— [IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md) 決定 1 と SC-07 の先例（`canRetry`）に揃え、**理由の文言を置く**。
消すだけだと「このソースは無効化できない（状態の問題）」と読めてしまい、権限の問題と区別できない。
**実効境界はサーバ側**であり、画面は表示制御にすぎない（[IADR-0039](../adr/IADR-0039_datasource-management-bff-and-role-gating.md) 決定 2）。

ルートのゲート（`RequireRole anyOf={[Admin, Operator]}`）は**変更しない**（閲覧は運用者に開いたまま）。

### 4. 実装 ADR を新設しない理由（判断の記録）

本作業の判断はいずれも**既存 IADR の適用**であり、新しい決定を含まない。

| 判断 | 依拠 |
| --- | --- |
| `AdminOnly` をグループへ積む（絞らない） | [IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) 決定 1（#501 が確立した形） |
| BFF と後段の両方を同時に狭める | [IADR-0044](../adr/IADR-0044_backend-service-authorization-defense-in-depth.md)（多層防御） |
| 画面のボタンを理由つきで消す | [IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md) 決定 1（SC-07 `canRetry` の先例） |
| 手動同期を運用者に残す | **計画の裁定**（planning#299）。実装判断ではない |
| SC-05 の同型逸脱を別 issue へ | [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4 |

**「決定していない」ことを記録するのが本節である。** IADR を作ると、既存の決定と同じ内容が
2 箇所に並び、片方が黙って古くなる。

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 運用者で `POST /datasources` が 403（サービス直） | `DataSourceAuthorizationTests` |
| 2 | 運用者で `DELETE /datasources/{id}` が 403（サービス直） | 同上 |
| 3 | 運用者で一覧・個別取得は 200 のまま | 同上（**対で固定する**） |
| 4 | 運用者で `POST /bff/datasources` が 403 | `BffDataSourceEndpointTests` |
| 5 | 運用者で `DELETE /bff/datasources/{id}` が 403 | 同上 |
| 6 | **運用者で `POST /bff/datasources/{id}/sync` は 202 のまま** | 同上（**planning#299 の追認を固定する**） |
| 7 | 管理者では登録・無効化が従来どおり成功する | 上記 2 ファイル |
| 8 | 画面が運用者に「ソース登録」「無効化」を出さない | `DataSourceManagementPage.test.tsx` |
| 9 | 画面が運用者に「手動同期」を**出す** | 同上 |
| 10 | 画面が管理者には 3 つとも出す | 同上 |

## 追随させる文書

- [`docs/api/BFF_bff-surface.md`](../../docs/api/BFF_bff-surface.md): `POST` / `DELETE` の認可列を **admin のみ**へ
- [`docs/screens/SC-06_datasource-management.md`](../../docs/screens/SC-06_datasource-management.md):
  §アクセス（書き込みロールを明記）・§未決事項 5 を**解消済みへ**・モック対応表の該当行
- [`docs/tests/SC-06_datasource-management.md`](../../docs/tests/SC-06_datasource-management.md): 上表の 10 件
- `docs/api/openapi.yaml`: 該当操作の `description`（**編集したら `pnpm run codegen` を必ず再実行する**。
  orval は `description` を JSDoc へ写すため、注記だけの編集でも生成物が動く。**#627 で実際に CI を落とした**）

## 検証記録（実測・すべて本作業の head で走らせた）

`node scripts/…` は**リポジトリのルートから実行する**（`src/` から走らせると相対パスが割れる。§事故記録）。

| 対象 | コマンド | 結果 |
| --- | --- | --- |
| バックエンド（knowledge） | `dotnet test knowledge/backend/backend.slnx` | **431 passed / 0 failed**（18 skipped は統合テストの環境依存） |
| バックエンド（platform） | `dotnet test platform/backend/backend.slnx` | **361 passed / 0 failed**（1 skipped） |
| 認可（後段） | `dotnet test … --filter DataSourceAuthorizationTests` | **15 passed**（本作業で 5 件追加） |
| 認可（BFF） | `dotnet test … --filter BffDataSourceEndpointTests` | **19 passed**（本作業で 4 件追加） |
| 整形（C#） | `dotnet format <slnx> --verify-no-changes`（両ユニット） | OK |
| 型 / lint / 整形（TS） | `pnpm run typecheck` / `lint` / `format:check` | OK（lint は warning 9・error 0。既存の `react-refresh` 警告） |
| 単体 ＋ カバレッジ | `pnpm run test:coverage` | **statements 96.38% / branches 90.53% / functions 91.70% / lines 96.38%**（床は 90 / 85 / 88 / 90。**割っていない**） |
| ビルド | `pnpm run build` | OK |
| チャンク予算 | `node scripts/check-chunk-budget.js` | **床を 577.92 kB → 578.06 kB へ更新**（+0.14 kB。増分は**新しい表示文言 1 件が Lingui カタログへ載った分**であり、初期チャンクの `messages.ts` に入る） |
| 外部送信 | `node scripts/check-static-egress.js --require src/platform/frontend/dist` | OK（23 ファイル・外部オリジン 0） |
| i18n カタログ | `pnpm run i18n` ＋ `node scripts/check-i18n-catalogs.js` | OK（**ja / en 両方に訳を入れた**。未翻訳のままだと `lingui compile --strict` が落ちる） |
| 契約スキーマ | `node scripts/check-contract-schema.js` | OK（62 型が baseline と一致・**破壊的変更なし**。認可の変更は DTO を動かさない） |
| テスト仕様の床 | `node scripts/check-test-spec-coverage.js --update` | **床を 74 → 75 対へ更新**（`SC-06 × DataSourceAuthorizationTests` を追加） |
| その他 | `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-landed-subjects` / `check-adr-numbering` / `check-bff-downstreams` / `check-test-traceability` / `check-unit-dependencies` / `check-unit-service-ownership` / `check-backend-libraries` | すべて OK |

**カバレッジ床（`src/vitest.config.ts` の `thresholds`）は上げない。** 実測は床より 6 ポイント以上高いが、
床は #619 以降どの PR も動かしておらず、**ここで全ユニット横断の床を上げるのは #628 の射程を超える**
（並走中の PR を巻き添えにする）。**本作業でカバレッジが下がっていないことは上表で示した。**

### 事故記録（同型 3 回目）

**`node scripts/check-i18n-catalogs.js` を `src/` から実行して `MODULE_NOT_FOUND` を出した。**
#627 で 2 回起こした型（`check-chunk-budget` / `check-static-egress`）と同じである。
**規約ではなく手順の問題**であり、検査器を足しても防げない（CI はルートから走るので常に緑になる）。
本仕様書の §検証記録 冒頭に注意を明記し、以後は**ルートから実行する**。

## 計画へのフィードバック

**本作業では新たな環流を生まない。** 手動同期の分は planning#299 で裁定を得て解決済みであり、
本 PR はその裁定を**追認して固定した**（コード変更なし・テストと文書で明示）。
**SC-05 の同型逸脱は実装側の是正**であり、計画への環流ではなく**本リポの issue #629** として起票した。
