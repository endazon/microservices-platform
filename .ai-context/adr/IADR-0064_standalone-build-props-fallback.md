---
title: IADR-0064 単独ビルド用フォールバック props はパスをプロパティへ束ねて MSB4092 を回避し、実ファイル同梱でコピペ事故を防ぐ
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - IADR-0060
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-14: 構成変更で完結する疎結合ユニット)
---

# IADR-0064: 単独ビルド用フォールバック props の MSB4092 回避と実ファイル同梱

- 状態: Accepted
- 日付: 2026-07-12
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-14（構成変更のみで完結する疎結合ユニット）
- 関連 ADR: [IADR-0060](./IADR-0060_submodule-unit-operations.md)（submodule 運用。本 IADR はその成果物 = 単独ビルド用フォールバック props の欠陥を是正する）
- 関連仕様書: `docs/specs/20260712_issue-256_fix-standalone-props-condition.md`、`templates/unit-template/README.md`、`docs/how-to/adding-a-unit-submodule.md`
- Issue: #256（修正案は AST#103 で実証済み）

## コンテキストと課題

[IADR-0060](./IADR-0060_submodule-unit-operations.md) は、追加可変機能ユニットを単独リポジトリでビルドする際のフォールバック `Directory.Build.props`
を定めた。submodule 配置時は本体 `src/Directory.Build.props`（単一情報源）を継承し、単独時のみ自前の既定を
効かせるため、親を `[MSBuild]::GetPathOfFileAbove(...)` で import-chain する構造である。

しかし当初の記載スニペットは、`Import` の `Condition` 属性に `GetPathOfFileAbove` を**直接**書いていた:

```xml
<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))"
        Condition="'$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))' != ''" />
```

`Condition` の外側シングルクォートと、関数引数（`'Directory.Build.props'` 等）の内側シングルクォートが
衝突し、MSBuild の条件式トークナイザがネストしたクォートを解釈できず **MSB4092** でビルド失敗する
（実測: `error MSB4092: 予期しないトークン "Directory" ... 文字の場所 35`）。テンプレートどおりに単独
ビルドすると必ず詰まる欠陥である。

## 検討した選択肢

**MSB4092 の回避方法**:
1. `Condition` から関数呼び出しを除き、パスを一旦プロパティへ束ねて単純なプロパティ参照で比較する（本決定）。
   要素内容（`<Prop>...</Prop>`）は条件パーサを通らないため、内側クォートがあっても MSB4092 は起きない。
2. `Condition` を `Exists(...)` に置換する: `Exists` にも関数引数の内側クォートが残り、同じネスト問題を招く。
3. `Condition` を外して常に `Import` する: 親が無い単独時に `Import Project=""` となり別のエラー/警告を生む。

**スニペットの提供形態**:
1. README にコード塊で記載するのみ（現状）: コピペ時に引用符・エスケープを取りこぼしやすく、まさに本件の
   温床。単独ビルドを試すたびに手作業で書き起こす必要がある。
2. **修正済みスニペットを実ファイル（`Directory.Build.props.sample` 等）としてテンプレートに同梱する（本決定）**:
   単独ビルド時は拡張子 `.sample` を外すだけで使える。ファイルとして CI/エディタで検証でき、コピペ事故を防ぐ。
   `.sample` 拡張子のままなら MSBuild は発見しないため、テンプレート位置や submodule 配置時に副作用がない。

## 決定

1. **プロパティ束ね**: パスを `ParentDirectoryBuildProps` プロパティへ束ね、`Import` の `Condition` は
   `'$(ParentDirectoryBuildProps)' != ''` の単純比較にする。`Import Project` にも同プロパティを使う。

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

2. **実ファイル同梱**: 修正済みフォールバックを `templates/unit-template/backend/Directory.Build.props.sample`
   として同梱する。単独時の CPM フォールバックも `Directory.Packages.props.sample` として同梱する。単独
   ビルド時のみ `.sample` を外して使う旨を README / how-to に明記する。

## 理由

- **挙動は不変**: プロパティへ束ねても評価結果は同じ。submodule 配置時は親（`src/Directory.Build.props`）を
  継承し、単独時のみ `TargetFramework` 等のフォールバックが効く（実測で両シナリオを確認）。
- **fail-safe**: 誤ったスニペットは「単独ビルドが常に MSB4092 で止まる」という分かりやすい失敗だが、修正で
  正常化する。実ファイル化により以後のコピペ起因の再発を断つ。
- **副作用ゼロ**: `.sample` 拡張子のままなら MSBuild の探索対象外。テンプレート位置・submodule 配置時とも
  単一情報源の上書き問題（[IADR-0060](./IADR-0060_submodule-unit-operations.md) 決定4）を再燃させない。

## 結果

- `templates/unit-template/backend/Directory.Build.props.sample`（新規）／`Directory.Packages.props.sample`（新規）。
- `templates/unit-template/README.md`: 単独ビルド節を修正案へ差し替え、実ファイル参照に更新。
- `docs/how-to/adding-a-unit-submodule.md` §5: 実ファイル参照へ追随。
- 検証: 現行スニペットで MSB4092 を再現 → 修正版で `dotnet build` 成功（単独）・親継承（配置時）を実 SDK で確認。

## フォローアップ

- サンプルユニットでの end-to-end 通し検証（別リポジトリ必須）は本リポジトリ内で完結できないため #230 に残す。

## 関連

- Supersedes: なし（[IADR-0060](./IADR-0060_submodule-unit-operations.md) の成果物の欠陥是正であり、決定自体は覆さない）
- Superseded by: なし
