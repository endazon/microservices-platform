---
title: 作業仕様書 — 標準構成 8 要素の .gitkeep を 55 件（＋雛形 1 件）適用する
type: spec
status: done
related_ids:
  - ADR-0030
  - NFR
  - IADR-0116
  - IADR-0117
  - IADR-0218
  - IADR-0219
author: claude
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md (§規範性・粒度・置き場 / §SharedKernel の粒度・Worker の追加)
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md
  - planning:projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md (決定 4)
related_specs:
  - "../adr/IADR-0219_sharedkernel-granularity-and-worker-standard-component.md"
  - "../adr/IADR-0218_gitkeep-standard-components-scope.md"
  - "20260817_iadr-0219_sharedkernel-worker-amendment.md"
---

# 作業仕様書: `.gitkeep` 55 件 ＋ 雛形 1 件の適用

## 1. 起点となる ID（トレーサビリティ）

- **`ADR-0030`**（バックエンドアプリケーション層の標準構成。ブランチ名の起点 ID）
- **無採番 `NFR`**（構成の可視化＝メタ作業。`.claude/rules/traceability.md`「無採番 `NFR` を許す 2 つの場合」の**場合 2**）

**本作業は [IADR-0219](../adr/IADR-0219_sharedkernel-granularity-and-worker-standard-component.md) 決定 3 が「適用は次の波で行う」と明記したものの実行である。**
判断は [IADR-0218](../adr/IADR-0218_gitkeep-standard-components-scope.md) 決定 3-2〜3-7 と [IADR-0219](../adr/IADR-0219_sharedkernel-granularity-and-worker-standard-component.md) 決定 1〜3 に尽きており、**本書で新たな判断はしない。**

## 2. 何をするか

計画 `12_backend-application-stack` §規範性・粒度・置き場:

> **8 つは全リポジトリ共通の標準構成である**（2026-08-17 に `Worker` を追加し 7 つ → 8 つ）。
> 実体のあるものは通常どおりプロジェクト（`.csproj`）として作る。
> **実体が無いものは、空のフォルダを作り `.gitkeep` だけを置く**（`.csproj` は作らない）。

## 3. 母集合 —— 引いた結果と、除外したものとその理由

**時点: 2026-08-17、worktree `/home/user/wt-gk55`、ブランチ `feat/ADR-0030-gitkeep-standard-components`、base = `docs/ADR-0030-sharedkernel-worker-amendment`（`1a73911a`）。**

### 3.1 対象サービスの列挙（**走査ではなく数え直した**）

```bash
git ls-files "src/*/backend/Services/*/src/*/*.csproj"   # → 11 行（.Api 9 / .Worker 2）
git ls-files "src/*/backend/Services/*/tests/*/*.csproj" # → 11 行（Tests 実体 11/11）
git ls-files "src/**/.gitkeep"                           # → 0 件
```

**11 サービス**: platform = `AuthorizationService` / `LlmGateway`、
knowledge = `AiAnalysisService` / `ConversionService` / `DashboardService` / `DataSourceService` /
`DocumentService` / `FeedbackService` / `IngestionService` / `RetrievalService` / `WikiService`。

### 3.2 要素ごとの内訳

| 要素 | 実体 | `.gitkeep` | 根拠 |
| --- | ---: | ---: | --- |
| `Api` / `Worker`（**排他**） | 11 / 11 | **0** | [IADR-0219](../adr/IADR-0219_sharedkernel-granularity-and-worker-standard-component.md) 決定 2。**実行入口は 1 サービスに 1 つ**であり「空の実行入口」は存在しない |
| `Application` | 0 / 11 | 11 | |
| `Domain` | 0 / 11 | 11 | |
| `Infrastructure` | 0 / 11 | 11 | |
| `Contracts` | 0 / 11 | 11 | |
| `SharedKernel` | 0 / 11 | **11** | [IADR-0219](../adr/IADR-0219_sharedkernel-granularity-and-worker-standard-component.md) 決定 1 により新たに対象 |
| `Tests` | 11 / 11 | **0** | 実体が 11/11 |
| **計** | | **55** | |

**`Api` / `Worker` の排他は実測で成立している**（両方持つサービス **0 件**）。

### 3.3 既存プロジェクトとの衝突（**0 件であることを実測**）

55 スロットのいずれにも既存ディレクトリは無い。**既存の `Contracts` / `Infrastructure` プロジェクトは
ユニット階層**（`src/*/backend/Shared/`）**にあり、サービス配下ではない** ——
これは裁定が認めた**併存**（per-service と per-unit）の形そのものである。

### 3.4 雛形

`templates/unit-template/backend/Services/SampleService/` は 8 要素中 **6 つが実体**
（`Api` / `Application` / `Contracts` / `Domain` / `Infrastructure` / `Tests`）。
`Worker` は `Api` と排他で対象外。**残る `SharedKernel` 1 件**が対象である
（[IADR-0218](../adr/IADR-0218_gitkeep-standard-components-scope.md) 決定 3-6 の「適用対象 0 件」は [IADR-0219](../adr/IADR-0219_sharedkernel-granularity-and-worker-standard-component.md) で覆った）。

### 3.5 黙って除外していないもの（規則 6）

| 除外 | 理由 |
| --- | --- |
| **`src/ai-stock-trading/`** | **別プロジェクトの submodule。向こうの issue である**（[IADR-0218](../adr/IADR-0218_gitkeep-standard-components-scope.md) 決定 3-7） |
| `src/*/frontend/` | 本標準は**バックエンド**の構成である |
| `src/packages/ui` | 同上（共有 UI パッケージ） |
| `src/platform/backend/Bff` / `src/knowledge/backend/Bff` | **サービスではない**（BFF は 11 サービスの数に入らない） |
| `src/*/backend/Shared/` | **ユニット階層**であり、サービス配下の 8 要素とは階層が違う |

## 4. 実装

1. **55 件の `.gitkeep` を作る。** パスは `src/<unit>/backend/Services/<Name>/src/<Name>.<要素>/.gitkeep`
   （フォルダ名 `<Name>.<要素>` は [IADR-0218](../adr/IADR-0218_gitkeep-standard-components-scope.md) 決定 3-3 のまま）
2. **雛形へ 1 件**: `templates/unit-template/backend/Services/SampleService/src/SampleService.SharedKernel/.gitkeep`
3. **`.gitkeep` は空ファイルとする**（[IADR-0218](../adr/IADR-0218_gitkeep-standard-components-scope.md) 決定 3-4。計画は「`.gitkeep` だけを置く」と書いている）
4. **`src/README.md` へ注記を足す**（[IADR-0218](../adr/IADR-0218_gitkeep-standard-components-scope.md) 決定 3-5）。**2 つの読み替えを書く**:
   - **空フォルダは「コードが無い」ことを意味しない** —— 層は各サービスの `Api` / `Worker` 配下
     （`Foundation/` / `Composable/` 等）に実在する。空なのは**プロジェクトとして分けていない**という意味である
   - **`src/README.md`「存在しない区分のフォルダは作らない（空フォルダを置かない）」との階層の違い** ——
     同記述は**プロジェクトの内側**（`Foundation/` / `Composable/` / `Adapters/` / `Connectors/`）に掛かる規則であり、
     **サービス直下の 8 要素には掛からない**

## 5. 追随不要と判断したもの（**実測して確かめた**）

- **`src/README.md` の L50 / L66 は追随不要** —— 既に `<Api|Worker>` の**排他の形**で書かれている
  （`tests/<ServiceName>.<Api|Worker>.Tests/` 等）。[IADR-0219](../adr/IADR-0219_sharedkernel-granularity-and-worker-standard-component.md) 決定 2 と矛盾しない

## 6. 検証

**[IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md) の順序**（`git add -A` → 検査器 → コミット → HEAD を読む検査器）。

- **`.gitkeep` の実数を数え直す**（走査ではなく計算）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` を**必ず回す**
- **`dotnet build` が両ユニットで通ること** —— `.gitkeep` は `.csproj` を作らないので
  ソリューションに影響しないはずだが、**「はず」で済ませない**
- **終了コードは判定ではない。判定行を読む**

## 7. やらないこと

- **`.csproj` を作らない**（計画が明示。空の `.csproj` を並べるとソリューションとビルド対象が無用に増える）
- **`src/ai-stock-trading` を触らない**
- **`.gitkeep` に内容を書かない**（決定 3-4）
