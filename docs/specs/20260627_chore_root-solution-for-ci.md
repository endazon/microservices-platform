---
title: 作業仕様書 — CI のルート実行に対応するルートソリューション配置
type: work-spec
status: in-progress
related_ids:
  - NFR
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/01_architecture-overview.md"
related_specs:
  - 20260627_chore_efcore-relational-version-pin.md
  - ../tech/tech-requirements.md
issue: "#34"
---

# 作業仕様書: CI のルート実行に対応するルートソリューション配置

## 目的

CI（`.github/workflows/ci.yml`）が **リポジトリルート**で `dotnet format` /
`dotnet restore` を実行する一方、ソリューション（`src/KnowledgePlatform.slnx`）と
`global.json` が `src/` 配下にしか無いため、以下のエラーで CI が失敗していた。

```
dotnet format --verify-no-changes
  System.IO.FileNotFoundException: Could not find a MSBuild project file or
  solution file in '/home/runner/work/microservices-platform/microservices-platform/'.

dotnet restore
  MSBUILD : error MSB1003: Specify a project or solution file.
```

これを解消し、PR #36（Issue #34）の CI を通過させる。

## 原因

- `ci.yml` の各ステップは作業ディレクトリ（＝リポジトリルート）で
  `dotnet format` / `dotnet restore` をパス指定なしで実行する。
- `dotnet` はカレントディレクトリのプロジェクト/ソリューションを探索するが、
  ルートには `.sln`/`.slnx`/`.csproj` が存在しない（すべて `src/` 配下）。
- `.github/workflows/` は GitHub App 権限で編集できないため、ワークフロー側で
  `working-directory` やパス指定を加える対処は採れない。

## 方針

リポジトリルートに、`src/` 配下の全プロジェクトを参照するソリューションと
SDK 設定を**追加**する（既存の `src/` レイアウトは変更しない）。

- `KnowledgePlatform.slnx`（ルート）: `src/KnowledgePlatform.slnx` を複製し、
  各 `Project Path` を `src/` プレフィックス付きに書き換えたもの。
- `global.json`（ルート）: `src/global.json` と同一（`sdk.version=8.0.0`,
  `rollForward=latestMajor`）。ルートから実行される `dotnet` の SDK 選択を
  `src/` と一致させる。

### 妥当性

- プロジェクトは `net10.0` を対象とする（`src/Directory.Build.props`）。CI は
  `global.json` の `rollForward=latestMajor` で .NET 10 SDK を選択しており、
  .NET 10 のツールは `.slnx` をネイティブに解釈できる。よってルート `.slnx` は
  既存ビルドと同じ SDK で問題なく解決される。
- `Directory.Build.props` / `Directory.Packages.props` は各 `.csproj` から
  ディレクトリを遡って解決されるため、ソリューションの位置に依存しない。
  ルートソリューション経由でも従来どおり適用される。

## 作業範囲

### 含むもの
- ルート `KnowledgePlatform.slnx` の追加（`src/` 配下 23 プロジェクトを参照）
- ルート `global.json` の追加

### 含まないもの
- `src/KnowledgePlatform.slnx` / `src/global.json` の削除・移動（既存維持）
- `.github/workflows/` の変更（権限により不可）
- ビルド/テスト内容・依存関係の変更

## 受け入れ基準

- [ ] CI の `dotnet format --verify-no-changes`（lint ジョブ）が
      「project/solution が見つからない」エラーで失敗しない
- [ ] CI の `dotnet restore`（build-and-test ジョブ）が MSB1003 で失敗しない
- [ ] ルートソリューションが `src/` 配下の全 23 プロジェクトを参照する
- [ ] `src/` 配下の既存ソリューション/設定に差分が出ない

## リスク・注意事項

- 本作業環境では .NET SDK が無効化されており `dotnet` を実走できない。最終的な
  解決は CI ログで確認する。
- ルートと `src/` で 2 つのソリューションを維持することになる。プロジェクト追加時は
  両ソリューションへの登録が必要（将来的にどちらかへ一本化する場合は別途検討）。
