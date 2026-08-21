---
title: 作業仕様書 — イベント型 → 発行元 / 購読先の対応表を機械生成し、移行前の基準として固定する
type: spec
status: done
related_ids:
  - NFR
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - "ADR-0027（メッセージング基盤 / Wolverine）"
  - "ADR-0030（バックエンドアプリケーション層標準）"
related_adrs:
  - IADR-0130
  - IADR-0209
issue: "#455"
related_issues:
  - "#441"
  - "#447"
  - "#449"
---

# 作業仕様書: イベント対応表を機械生成し、移行前の基準として固定する

## 起点となる計画書（トレーサビリティ）

- 機能要求: 該当なし（**NFR**。メッセージング基盤の移行安全性）
- 関連計画 ADR: `ADR-0027`（Wolverine 採用）／ `ADR-0030`（アプリケーション層標準）
- 親 issue: **#455**（バックエンドアプリケーション層標準への全面移行）の子 C

## 目的 —— なぜ移行より先に作るのか

`#455` は Wolverine 移行チェックリスト 8 手順の**手順 1** を次のように定めている。

> 移行**前**にイベント型 → 購読サービスの対応表を機械的に作る（後の検査の基準になる）

計画は同時にこう警告している。

> **うち 2 つの退行はビルド・ユニットテスト・トポロジ検査をすべて通過したまま、例外もログも出さずに
> 業務イベントを失う**

**「壊れたことが赤で分からない」種類の退行**であるため、**移行の前に正解表を凍結しておかないと、
移行後に「これで合っているのか」を判定する基準が無くなる。** 本作業はその基準を作る。

## 着手前の実測（母集合）

引いたコマンドと結果を残す（`.claude/rules/traceability.repo.md` §是正・追随の母集合の取り方）。

| 軸 | コマンド | 結果 |
| --- | --- | --- |
| イベント契約 | `git ls-files 'src/knowledge/backend/Shared/Knowledge.Contracts/Events/*'` | **6 件**（`RawDocumentFetched` / `DocumentNormalized` / `DocumentUpdated` / `DocumentDeleted` / `IngestionCompleted` / `IngestionRequested`） |
| 購読（MassTransit） | `git grep -n "IConsumer<" -- 'src/**/*.cs'` | **5 件**（下表） |
| 購読（Wolverine） | `git grep -n "using Wolverine"` | **0 件**（未移行） |

**購読の実測**:

| 購読サービス | イベント |
| --- | --- |
| ConversionService | `RawDocumentFetched` |
| DocumentService | `DocumentNormalized` |
| **IngestionService** | **`DocumentUpdated`** |
| WikiService | `DocumentDeleted` |
| **WikiService** | **`DocumentUpdated`** |

🔴 **`DocumentUpdated` の購読は 2 件である**（IngestionService ＋ WikiService）。**これが本作業で
最も重要な固定対象**である —— Wolverine 移行の手順 3（キュー名にサービス名を前置しない）を
誤ると、**2 つの購読者が同一キューを競合し、片方だけがメッセージを受け取る**。ビルドも
ユニットテストも通り、例外もログも出ないまま、**業務イベントの半分が消える**。

**除外したもの**: `src/ai-stock-trading`（submodule。別プロジェクトの契約）、テストプロジェクト
（`tests/` 配下・`*.Tests`。フィクスチャの発行を実配線と数えない）、`bin/` `obj/`。

## スコープ

- `scripts/check-event-topology.js` を新設し、**発行元と購読先を走査で発見**して対応表を作る
- `scripts/event-topology-baseline.json` に固定し、**増減の両方向**を ratchet で止める
- `.github/workflows/ci.yml` に検査を足す（既存の検査ジョブへ相乗り）

### スコープ外

- **Wolverine への移行そのもの**（子 D 以降）。本作業は**現状を凍結するだけ**で、挙動を変えない
- **キュー名・トポロジの検査**（手順 3〜6）。共通ヘルパが無い段階では検査対象が無い
- **実ブローカでの結合テスト**（手順 8）。子 L の射程

## 設計

### 1. 列挙を持たない

イベント型は `*.Contracts/Events/*.cs` の走査で発見する。発行元・購読先も走査で見つける。
**baseline に載るのは「発見した結果」であり、走査対象の指定ではない。**

### 2. 両方向の ratchet

- **減った** → 違反（購読が消えた ＝ イベントを失う退行そのもの）
- **増えた** → 違反（baseline の更新を強制する。黙って増やさせない）

### 3. 0 件走査で緑を返さない

イベント契約が 0 件、または購読が 0 件なら **exit 1**（走査が壊れた可能性）。

### 4. 購読 0 件のイベントは notice で必ず出す

`IngestionCompleted` / `IngestionRequested` は**誰も購読していない**。これは違反ではないが、
**黙っていると「検査した結果 0 件」と「そもそも見ていない」が区別できない**（[[IADR-0130]]）。

### 5. MassTransit と Wolverine の**両方**の記法を読む

移行中は両方が同居し得る。`IConsumer<T>`（MassTransit）と `Handle(T)` / `Consume(T)`
（Wolverine 規約）の双方を購読として数える。**移行しても表が変わらないことが、移行が正しいことの証拠**になる。

## 受け入れ基準

1. `node scripts/check-event-topology.js` が **exit 0** で、イベントごとの発行元・購読先を出力する
2. `--self-test` があり、CI が本走査の前に呼ぶ
3. baseline が **`DocumentUpdated` の購読 2 件**を明示的に持つ
4. **変異試験 A**: `DocumentUpdated` の購読を 1 つ消すと **exit 1**
5. **変異試験 B**: 購読を 1 つ増やすと **exit 1**（baseline 更新を強制）
6. **変異試験 C**: イベント契約の走査を空にすると **exit 1**（0 件走査で緑を返さない）
7. 購読 0 件のイベント 2 件が **notice** に出る
8. `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑（検査器の母集合 ratchet 36 → 37 を追随）

## 実行結果（証跡）

### 生成された対応表（＝移行前の正解表）

```console
$ node scripts/check-event-topology.js
notice: 購読が 0 件のイベントが 2 件ある（違反ではない）:
  IngestionCompleted（発行元: knowledge/IngestionService） / IngestionRequested（発行元: なし）
[check-event-topology] OK: イベント 6 件 / 購読 5 件が baseline と一致。
  DocumentDeleted:    発行 [knowledge/DocumentService]   → 購読 [knowledge/WikiService]
  DocumentNormalized: 発行 [knowledge/ConversionService] → 購読 [knowledge/DocumentService]
  DocumentUpdated:    発行 [knowledge/DocumentService]   → 購読 [knowledge/IngestionService, knowledge/WikiService]
  IngestionCompleted: 発行 [knowledge/IngestionService]  → 購読 [-]
  IngestionRequested: 発行 [-]                           → 購読 [-]
  RawDocumentFetched: 発行 [knowledge/DataSourceService] → 購読 [knowledge/ConversionService]
```

### 受け入れ基準

| # | 基準 | 実行したコマンド | 結果 |
| --- | --- | --- | --- |
| 1 | 本走査が exit 0 で対応表を出す | `node scripts/check-event-topology.js` | **EXIT=0**（上記） |
| 2 | `--self-test` があり CI が本走査の前に呼ぶ | `node scripts/check-event-topology.js --self-test` | **self-test OK: 8 件**。`ci.yml` の `event-topology` ジョブが本走査の前に呼ぶ |
| 3 | baseline が `DocumentUpdated` の購読 2 件を持つ | `scripts/event-topology-baseline.json` | ✅ `["knowledge/IngestionService", "knowledge/WikiService"]` |
| 7 | 購読 0 件のイベント 2 件が notice に出る | 同上 | ✅ `IngestionCompleted` / `IngestionRequested` |
| 8 | 伴走テストが緑（検査器の母集合 36 → 37） | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **✓ 575 tests passed** |

### 変異試験（**3 本すべて実測。素通りは無い**）

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| A | `IngestionService` の `IConsumer<DocumentUpdated>` を `IConsumer<DocumentNormalized>` へ差し替える | exit 1 | **EXIT=1**。**2 件**検出 —— 「`DocumentUpdated` の購読先が減った: knowledge/IngestionService」＋「`DocumentNormalized` の購読先が増えた」 |
| B | `DashboardService` に `IConsumer<DocumentDeleted>` を足す | exit 1 | **EXIT=1**「`DocumentDeleted` の購読先が増えた: knowledge/DashboardService」 |
| C | 走査ルートを空にする | イベント 0 件 → exit 1 | **発見イベント数 0**（`main()` は 0 件で exit 1 にする） |

各変異のあと**必ず復旧を確認**した（本走査 EXIT=0・`git status --porcelain -- src/` が空）。

> **変異 B は 1 度空振りした。** 最初は対象ファイルの `using ` 行を置換して probe を入れようとしたが、
> **そのファイルに `using ` が 1 行も無かった**ため置換が何もせず、EXIT=0 になった。
> **「検査が落ちなかった」ではなく「変異が入っていなかった」**のであり、`grep -c "^using "` で
> 0 件であることを確かめてから、行を追記する形へ変えて測り直した。
> **変異試験は「変異が実際に入ったこと」を確かめてから判定する。**

### 引いた母集合と除外理由

| 軸 | コマンド | 結果 |
| --- | --- | --- |
| イベント契約 | `*.Contracts/Events/*.cs` の走査 | **6 件** |
| 購読（MassTransit） | `IConsumer<T>` | **5 件** |
| 購読（Wolverine） | `Handle(T)` / `Consume(T)` | **0 件**（未移行。移行後もこの表が変わらないことが移行の正しさの証拠になる） |

**除外したもの**: `src/ai-stock-trading`（submodule。別プロジェクトの契約名前空間）、
テストプロジェクト（`tests/` 配下・`*.Tests`。**フィクスチャの発行を実配線と数えない**）、
`bin/` `obj/` `node_modules/`。

**除外が効いていることの確認**: 除外前に手で `git grep` したときは
`DocumentDeleted <- WikiService`（3 件）や `RawDocumentFetched <- ConversionService` が出ていたが、
これらは**すべてテストコード**だった。検査器はこれらを正しく落としている。
