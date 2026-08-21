---
title: 単独ビルド用フォールバック props の MSB4092 修正（Issue #256・入れ子クォート）
type: spec
status: done
related_ids:
  - FR-14
  - IADR-0060
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-14: 構成変更で完結する疎結合ユニット)
related_specs:
  - "../adr/IADR-0060_submodule-unit-operations.md"
  - "../adr/IADR-0064_standalone-build-props-fallback.md"
  - "../../docs/how-to/adding-a-unit-submodule.md"
  - "../../templates/unit-template/README.md"
---

# 仕様書: 単独ビルド用フォールバック props の MSB4092 修正（Issue #256）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-14（構成変更のみで完結する疎結合ユニット）
- 実装判断: [IADR-0060](../adr/IADR-0060_submodule-unit-operations.md)（submodule 運用）／本修正の決定は [IADR-0064](../adr/IADR-0064_standalone-build-props-fallback.md)
- Issue: #256（IADR-0060 フォローアップ／AST#103 で実証済み）

## 目的・背景

[IADR-0060](../adr/IADR-0060_submodule-unit-operations.md) で整備した「ユニットを単独リポジトリでビルドする際のフォールバック `Directory.Build.props`」
スニペット（`templates/unit-template/README.md` §単独ビルド・`docs/how-to/adding-a-unit-submodule.md` §5 参照）が、
MSBuild の `Condition` 属性で `GetPathOfFileAbove('Directory.Build.props', '...')` の**内側シングルクォートを
条件パーサが解釈できず MSB4092 でビルド失敗**する。テンプレートどおりに単独ビルドすると詰まるため修正する。

再現（現行スニペット、`dotnet build`）:

```
error MSB4092: 予期しないトークン "Directory" が、条件
"'$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))' != ''"
の文字の場所 35 で見つかりました。
```

原因: `Condition` の外側クォートと `GetPathOfFileAbove` 引数の内側クォートが衝突する。MSBuild の条件式
トークナイザはネストしたシングルクォートを扱えない。

## 対象範囲

- 対象（変更）:
  - `templates/unit-template/backend/Directory.Build.props.sample`（**新規・実ファイル**）: 修正済みスニペットを
    実ファイルとして同梱し、コピペ事故を防ぐ（Issue #256 推奨項目）。単独ビルド時のみ拡張子 `.sample` を外して使う。
  - `templates/unit-template/backend/Directory.Packages.props.sample`（**新規・実ファイル**）: 併せて単独時に必要な
    CPM フォールバックの雛形を同梱（README 記載のみだった `Directory.Packages.props` を実ファイル化）。
  - `templates/unit-template/README.md`: 単独ビルド節のスニペットを修正案（プロパティ束ね）へ差し替え、`.sample`
    実ファイルへの参照に更新。
  - `docs/how-to/adding-a-unit-submodule.md` §5: `.sample` 実ファイル参照へ追随。
  - `docs/adr/IADR-0064`（**新規**）: 修正パターン（プロパティ束ね）と実ファイル同梱の決定。
- 対象外（本リポジトリ内で完結不可・#230 のフォローアップに含む）:
  - サンプルユニットでの end-to-end 通し検証（別リポジトリ作成が必須）。

## 実装方針（修正案・AST#103 実証済み）

パスを一旦プロパティ（`ParentDirectoryBuildProps`）へ束ね、`Condition` は単純なプロパティ参照にする。

```xml
<Project>
  <PropertyGroup>
    <ParentDirectoryBuildProps>$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))</ParentDirectoryBuildProps>
  </PropertyGroup>
  <Import Project="$(ParentDirectoryBuildProps)" Condition="'$(ParentDirectoryBuildProps)' != ''" />
  <PropertyGroup Condition="'$(TargetFramework)' == ''">
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>13</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

- プロパティ値（要素内容）にはシングルクォートが含まれてよい（条件パーサを通らないため MSB4092 は起きない）。
- `Condition` は `'$(ParentDirectoryBuildProps)' != ''` の単純比較のみになり衝突しない。
- 挙動は不変: submodule 配置時は親（`src/Directory.Build.props`）を継承し、単独時のみフォールバック既定が効く。

## 受け入れ基準（Issue #256）との対応

- [x] テンプレートどおり作成したユニットが単独で `dotnet build` でき、MSB4092 が発生しない
  → 修正版フォールバック props + 最小 csproj で `dotnet build` 成功（0 エラー）を確認。
- [x] submodule 配置時は親を継承し、単独用既定で上書きしない
  → 親 props を上位に置いた模擬で `TargetFramework` が親由来（net10.0）・単独フォールバック未発火を確認。

## 検証（TDD 赤→緑・実 SDK）

1. **赤**: 現行スニペットの `Directory.Build.props` + 最小 csproj → `dotnet build` で `error MSB4092` を再現。
2. **緑（単独）**: 修正版（`.sample` 相当）→ `dotnet build` 成功、`TargetFramework=net10.0` が単独で適用。
3. **緑（配置時）**: 上位に親 props を置く模擬 → 親 marker 継承・単独フォールバック未発火（`StandaloneMarker` 空）。
4. `node scripts/check-doc-links.js` → 破損 0（README / how-to のリンク実在）。
5. テンプレートは本体ビルド・workspaces・依存検査の対象外（`src/` 外配置）であることを確認。

## 実装判断・フォローアップ

- 修正パターン（プロパティ束ね）と実ファイル同梱（`.sample`）の決定は [IADR-0064](../adr/IADR-0064_standalone-build-props-fallback.md) に記録。
- サンプルユニット通し検証は本リポジトリ外のため #230 のフォローアップに残す（本 PR は `Closes #256`）。
