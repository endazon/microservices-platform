---
title: 作業仕様書 EF 生成コードをカバレッジ集計から除外し、床を置き直す（#571）
type: spec
status: draft
related_ids: [NFR, IADR-0118, IADR-0123, IADR-0138]
author: Claude
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# 仕様書: EF 生成コードをカバレッジ集計から除外し、床を置き直す（#571）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（**NFR**。品質・保守性——再実装期間中の退行検知の精度）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: [IADR-0118](../adr/IADR-0118_backend-coverage-floor.md)（床の方式・値の置き方）／
  [IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md)（class 単位走査・
  `<class filename>` によるユニット帰属・二重記載の扱い）／
  [IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md)（除外ユニットの単一情報源）／
  **本作業で起票した [IADR-0138](../adr/IADR-0138_coverage-exclude-generated-code.md)**
- 計画書リンク:
  02_requirements/01_requirements.md（計画リポ）
- 関連 issue: [#571](https://github.com/endazon/microservices-platform/issues/571)。
  発端は [PR #568](https://github.com/endazon/microservices-platform/pull/568)。

## 目的・背景

PR #568 は **EF マイグレーションを 1 本追加しただけ**で `Check backend coverage floor` に止められた。
原因は**生成コードがカバレッジ集計に入っていること**である。

床の余裕は [IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) の追記どおり
**line +0.14pt / branch +0.26pt** しかなく、被覆しようのない生成コードが 150 行増えれば割れる。
`Migrations/` 配下と `*ModelSnapshot.cs` を集計から落とし、新しい定義での実測に合わせて床を置き直す。

## 対象範囲

- 対象:
  - [`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js) の改修（除外・診断・`--self-test`）
  - [`src/coverage-floor.json`](../../src/coverage-floor.json) の床の置き直しと `$comment` の更新
  - [IADR-0138](../adr/IADR-0138_coverage-exclude-generated-code.md) の起票
  - 床の値を書いた文書（[TEST_STRATEGY](../../docs/tests/TEST_STRATEGY.md) /
    [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 6 /
    [IADR-0118](../adr/IADR-0118_backend-coverage-floor.md) 決定 2）の追随
- 対象外:
  - `scripts/scripts.test.js`（[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md) 分類 A。**変更しない**）
  - `.github/workflows/`（権限外。`ci.yml` は既に `check-coverage-floor.js` を呼んでおり変更不要）
  - フロントのカバレッジ（[IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md) / `src/vitest.config.ts`）
  - 分岐（branch）の定義変更。生成コードの分岐は 0 のため床 `17` は据え置く
  - `<class filename>` のユニット帰属規則そのもの（IADR-0123 決定 2 を変えない）

## 設計

決定の詳細と棄却案は [IADR-0138](../adr/IADR-0138_coverage-exclude-generated-code.md) を正とする。実装は次の 4 点。

1. **`isGeneratedFilename(filename)`** を足す。`Migrations/` を区切り付きの一区画として含む、または
   `*ModelSnapshot.cs` で終わるパスを真とする。
2. `parseCobertura` で、**ユニット除外（IADR-0123 決定 1）を通したあと**に生成コードを落とす
   （AST 由来の行を二重計上しないため）。落とした量は `generated`（行 / 被覆 / 分岐 / クラス数 /
   ユニット別内訳 / filename 例）として返す。
3. 診断（既定出力・`$GITHUB_STEP_SUMMARY`）へ除外量と「生成コードを戻したときの値」を出す。
   coverlet の `lines-valid` との照合（IADR-0123 決定 4）は**すべての除外を戻した値**で行う
   （`beforeExclusion` に生成分を足し戻す）。
4. 生成コードが 0 行なら **notice**（fail でも warn でもない。IADR-0118 決定 6 の段階ポリシー）。

### 除外パターンをどう決めたか（実測）

**形を仮定して書くと「除外したつもりで素通り」になる**（IADR-0123 が名指しした失敗）。よって
実レポート（develop `3804511` / Release / 14 件）の `<class filename>` を先に見た。

| `<sources>` | `<class filename>` の実例 |
| --- | --- |
| `/w/src/` | `knowledge/backend/Services/WikiService/src/WikiService.Api/Migrations/20260626150858_InitialCreate.cs` |
| `/w/src/` | `…/Migrations/20260626150858_InitialCreate.Designer.cs` |
| `/w/src/platform/backend/` | `Services/AuthorizationService/src/AuthorizationService.Api/Migrations/AuthorizationDbContextModelSnapshot.cs` |

- 相対の先頭が揃わない（`knowledge/…` と `Services/…`）ため、**先頭一致では書けない**。
- 3 種（本体 / Designer / ModelSnapshot）はすべて `Migrations/` の下に出る。
- `Migrations/` の外の `*ModelSnapshot.cs` は **0 件**（`git ls-files` と実レポートの双方で確認）。
  それでも別規則を残すのは、出力先を変えたときの取りこぼしを避けるためである。
- **区切り付きで見る**——`MigrationsHelper.cs` / `MyMigrations/` / `ModelSnapshotBuilder.cs` を
  落とさないため（`--self-test` に負のケースとして固定した）。

### 床の導出

**CI の実測値を直接読む手段が無い**（`notice` はチェックのアノテーションに現れず、ジョブログの
署名 URL はプロキシに拒否される）。よって次のとおり**導出**した。値は
[`src/coverage-floor.json`](../../src/coverage-floor.json) の `$comment` にも測定条件つきで記録した。

| 項 | 値 | 出所 |
| --- | --- | --- |
| 基準（旧定義の CI 実測） | `line 34.14%（9314/27280）` | IADR-0123 の CI run 30886437108（run_number 1144）/ `build-and-test` / commit `594117a` / Release / 14 件 |
| 生成コードの行数 | 2310 行（45 クラス・**分岐 0**） | 本作業のローカル実測（develop `3804511`）。`594117a..HEAD` に `*/Migrations/*` の変更が無いことを `git log` で確認 |
| 生成コードのうち CI で被覆される行 | 933〜969 行 | 933 = 本作業のローカル実測（下記「生成コードは CI で被覆される」）。969 = 同レポートの生成行数（上限） |
| 導出 | `(9314 − 933) / (27280 − 2310) = 33.56%` ／ 上限側 `(9314 − 969) / (27280 − 2310) = 33.42%` | — |
| **置いた床** | **`line 33` / `branch 17`** | 整数へ切り下げ（余裕 0.42pt 以上）。branch は生成コードの分岐が 0 のため据え置き |

> **`line 34` → `33` は ratchet の引き下げ（退行）ではない。** 測定基準が変わったための置き直しであり、
> [IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) 決定 7 が #468 で行ったのと
> 同じ性質の作業である（あちらは切り下げ結果が同値だったため据え置きになった）。
> **旧定義の 34 と新定義の 33 は分母・分子が違うため直接比較できない。**

## 受け入れ基準

- [x] `Migrations/` 配下と `*ModelSnapshot.cs` が集計から落ちる（実レポートで確認）
- [x] 落とした行数・被覆行数・分岐・ユニット別内訳・除外前後の値が診断に出る
- [x] 生成コードが 0 行のときに notice が出る（素通りに気付ける）
- [x] 集計対象外ユニット（AST）配下の生成コードは**ユニット除外側**で数え、二重計上しない
- [x] `branch` の床は `17` のまま（生成コードの分岐は 0 で、除外しても分岐率が動かないことを実測）
- [x] `node scripts/check-coverage-floor.js --self-test` が拡張され exit 0
- [x] `node scripts/scripts.test.js`（**変更せず**）が通る
- [x] 生成コードを 1 本足しても除外後の実測値が動かない（変異試験）
- [x] 除外を外すと集計値が改修前の値へ戻る（変異試験）
- [x] 床を実測より上へ上げると fail する（変異試験）

## テスト方針

- `--self-test`（同スクリプト内）へ次を追加する。`scripts/scripts.test.js` は**変更しない**。
  - `isGeneratedFilename` の正のケース（実レポートで観測した 2 形 ＋ 絶対パス ＋ Windows 区切り ＋
    `Migrations/` 外の ModelSnapshot）と**負のケース**（`MigrationsHelper.cs` / `MyMigrations/` /
    `ModelSnapshotBuilder.cs` / `null` / `''`）
  - `parseCobertura` が生成コードを落とし、別枠で数え、ユニット別にも数えること
  - `aggregateReports` の `beforeGeneratedExclusion` と `beforeExclusion`（coverlet 照合用）の使い分け
  - `formatDiagnostics` の 3 行（除外量 / 戻した値 / ユニット別の内訳）
  - `attributionMessages` の notice（生成 0 行）と、生成が落ちているときに notice を出さないこと
  - 除外ユニット配下の生成コードを二重計上しないこと
- 実レポートに対する成立確認は **CI 実走を正とする**（IADR-0123 の運用。手元の SDK の在否に依存しない）。

## 検証（実測）

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-coverage-floor.js --self-test` | **自己試験 63 件 OK** / exit 0（着手前 **41 件** → +22。着手前の値は `git show HEAD:` の版を実行して実測） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **265 tests passed** / exit 0（`scripts.test.js` は未変更） |
| `REQUIRE_REPO_TESTS=1 GITHUB_ACTIONS=true node scripts/scripts.test.js` | 265 tests passed / exit 0（フィクスチャ由来のアノテーション漏れ 0 件） |
| `node scripts/check-doc-links.js --self-test` | 自己試験 34 件 OK / exit 0 |
| `node scripts/check-doc-links.js` | OK: **440 件**の Markdown に破損リンクなし / exit 0 |
| `node scripts/check-commit-messages.js --base origin/develop` | OK / exit 0 |
| `node -e "JSON.parse(…coverage-floor.json)"` | valid JSON / `line 33` / `branch 17` |
| `node scripts/check-coverage-floor.js`（実レポート 14 件・床 33） | `exit 0` / `line 33.03%（8281/25074）` / `branch 17.04%（1526/8954）` / `OK: 床を下回っていません。` |

### バックエンドの実走（ローカル）

測定条件: develop `3804511` / .NET SDK 10.0（`mcr.microsoft.com/dotnet/sdk:10.0` コンテナ）/
Release 構成 / レポート **14 件**（AST は対象外）。

| 条件 | 生成コード（行 / 被覆） | 生成込みの実測 | 生成を除いた実測（＝床の判定値） |
| --- | --- | --- | --- |
| 統合テストを走らせない（既定のローカル。18 件 skip） | 2310 / **0** | `line 26.38%（7223/27384）` / `branch 15.77%` | `line 28.81%（7223/25074）` / `branch 15.77%` |
| 統合テストを走らせた（CI 相当。35/39 成功） | 2310 / **933** | `line 33.65%（9214/27384）` / `branch 17.04%` | **`line 33.03%（8281/25074）`** / `branch 17.04%` |

### 生成コードは CI で被覆される（本作業で判明した重要な事実）

**issue #571 の前提（生成コードは 0 被覆なので除外すれば率が上がる）は、ローカル実行でのみ正しい。**
CI では統合テストが走り、`WebApplicationFactory` が `Program.cs` の起動処理まで実行するため、
**起動時 `MigrateAsync()` が migration の `Up()` と Designer の `BuildTargetModel()`、
`ModelSnapshot` の `BuildModel()` を実行する**。

Docker Hub からのイメージ取得は組織のエグレスポリシーで拒否される（`production.cloudfront.docker.com`
が 403）ため Testcontainers は使えない。代わりに **SDK コンテナへ PostgreSQL と RabbitMQ を導入して
起動し、統合テストを実走**させて測った（fixture の接続先だけを環境変数で差し替える一時パッチ。計測後に撤去）。

| 観測点 | 実測 |
| --- | --- |
| `AuthorizationService` のマイグレーション内訳 | `Up 56/56` 被覆 / `BuildTargetModel 154/154` / `BuildModel 83/83` / **`Down 0/18`** |
| 起動処理が走る証拠（`FeedbackService.Api/Program.cs`） | `if (db.Database.IsRelational())` が `hits=3`、その内側の `MigrateAsync()` が `hits=0`（InMemory のため） |
| 生成コードの被覆率（CI 相当） | `933/2310 = 40.4%` — **全体（約 34%）より高い** |

**したがって生成コードを除くと比率は下がる。** 床が `34 → 33` になるのはこのためである。

### 変異試験

いずれも上記「統合テストを走らせた（CI 相当）」の 14 レポートに対して実施した。

| # | 変異 | 期待 | 実測 | 判定 |
| --- | --- | --- | --- | --- |
| M1 | `isGeneratedFilename` を常に `false`（除外を無効化） | 改修前の集計値へ戻る | `line 33.03%（8281/25074）` → **`33.65%（9214/27384）`**。除外量の診断も `0 クラス / 0 行` になる | **落ちた（検出）** |
| M2a | 生成コードを 1 本追加（154 行・0 被覆の `Migrations/…_AddSyntheticMarker.cs` をレポートへ注入）・**除外あり** | 実測値が動かない | `line 33.03%（8281/25074）`。**追加前と完全に同値** | **意図どおり（不動）** |
| M2b | 同じレポートで除外を無効化 | 実測値が下がる（PR #568 の失敗モード） | `33.65%（9214/27384）` → **`33.46%（9214/27538）`**（−0.19pt） | **落ちた（検出）** |
| M3 | 床を実測より上へ（`line` を 33 → 34） | fail | `exit=1` / `line: 実測 33.03% < 床 34%` | **落ちた（検出）** |
| M3b | `branch` を 17 → 18 | fail | `exit=1` / `branch: 実測 17.04% < 床 18%` | **落ちた（検出）** |
| M4 | 判定パターンを実在しない形へ壊す（`Migrations/` → `Migration/`、`ModelSnapshot.cs` → `ModelSnapshotX.cs`） | 除外 0 行 ＋ notice | 除外 `0 クラス / 0 行`、`::notice::…生成コード…由来の行は 0 行でした` を出力。**ただし `exit=0`**（床 33 に対し実測 33.65% のため） | **notice で検出（fail はしない＝設計どおり）** |
| M4b | 同上の壊れたパターンで `--self-test` | 落ちる | **12 件 FAIL** / exit 1 | **落ちた（検出）** |
| — | 基準（無変異） | 通る | `exit=0` / `line 33.03%` / `branch 17.04%` / `OK: 床を下回っていません。` | — |

**素通りしたもの（隠さない）**:

- **M4（パターン破壊）は CI を fail させない。** notice と `--self-test`（M4b）でしか捕まらない。
  これは [IADR-0138](../adr/IADR-0138_coverage-exclude-generated-code.md) 決定 3 の意図した設計
  （EF の出力先変更で正常に 0 件になり得るため fail にしない）だが、**「notice を読む運用」に
  依存する穴**であることを明示する。
- **床 33 そのものは本作業では検証できていない。** ローカルの CI 相当実測は `33.03%` で床 33 を
  0.03pt しか上回らない。CI 値（導出 33.42〜33.56%）で通ることは**本 PR の CI 実走まで未検証**である
  （ローカルは統合テストが 35/39 のため CI より低く出る）。

## 計画書との差異

- 差異: なし（NFR 起点。計画書の要求に反する実装は無い）

## issue #571 の指示との差異（重要）

- **issue / 起票時の指示は「床（`backend.line`）を引き上げる」ことを前提としていたが、本作業は
  `34 → 33` へ置き直した。** 前提（生成コードは CI でも 0 被覆）が実測で否定されたためである
  （上記「生成コードは CI で被覆される」）。指示どおり引き上げると **CI が確実に赤くなる**
  （新定義での CI 実測は約 33.5% と導出され、床 34 でも 35 でも下回る）。
- **これは ratchet の引き下げ（退行）ではない。** 測定基準の変更に伴う置き直しであり、
  [IADR-0118](../adr/IADR-0118_backend-coverage-floor.md) 決定 2 /
  [IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) 決定 7 が定める作法に沿う。
  根拠と導出は本書と [IADR-0138](../adr/IADR-0138_coverage-exclude-generated-code.md) 決定 4・5、および
  [`src/coverage-floor.json`](../../src/coverage-floor.json) の `$comment` に残した。
- **issue の狙い（マイグレーション追加で床が割れる状態を塞ぐ）は達成している**——M2a のとおり、
  生成コードを 154 行足しても実測値は 1 桁も動かない。

## 未決事項

1. **新定義での CI 実測値**（本 PR の CI 実走で確定する）。導出値（33.42〜33.56%）から大きく外れた
   場合は、生成コードの被覆量の見積り（933〜969 行）を疑い、実測を読んで床を置き直す。
2. **IADR の採番**。`docs/adr/` の最大は `IADR-0135` だが、`IADR-0136`（PR #567）/ `IADR-0137`（PR #568）が
   未マージのまま予約されているため **`IADR-0138`** を採った。索引（[`docs/adr/README.md`](../adr/README.md)）は
   `0136` / `0137` が欠番の状態で本 PR がマージされる可能性がある（各 PR のマージで埋まる）。
