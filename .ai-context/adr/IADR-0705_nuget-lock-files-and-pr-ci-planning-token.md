---
title: IADR-0705 本リポジトリの 2 ユニットの NuGet の推移的な依存を lock ファイルで固定し、CI とイメージのビルドは locked mode で restore する。PR の CI へ計画リポジトリを読むトークンを渡さない
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0232, IADR-0423, IADR-0581]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_nuget-lock-and-pr-ci-token.md
---

# IADR-0705: NuGet の lock ファイルと PR の CI のトークン

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「全部直してから稼働」）・claude（方式）

## 起点・関連

- 要求 **NFR**（供給網と CI の統制。該当する採番が無いため無採番）。前提: IADR-0232（CI の構成・NuGet のキャッシュ）・IADR-0423（計画 ID レンジの突合）・
  IADR-0581（Dependabot の NuGet）。
- 作業仕様書: `.ai-context/specs/20261107_nuget-lock-and-pr-ci-token.md`。

## コンテキストと課題

- 直接の依存は Central Package Management で固定しているが、推移的な依存の解決結果は固定していなかった（追跡下の `packages.lock.json` が 0 件）。
  イメージのビルドの restore も同じだった。
- `ci.yml` は `pull_request` でも動き、計画 ID レンジの突合のステップへ `PLANNING_REPO_TOKEN` を渡していた。

## 決定

### 決定 1 — lock ファイルは 2 ユニットに限って有効にする

- `src/Directory.Build.props` で、本ファイルからの相対パスが `platform/backend/` か `knowledge/backend/` で始まるプロジェクトにだけ
  `RestorePackagesWithLockFile=true` を付ける。lock は `dotnet restore` で生成し、コミットする（39 本）。
- 本ファイルは import-chain で AST と雛形の試しビルドにも届くため、条件なしにしない。AST の lock は AST 側が決める。
- CPM の方式（`ManagePackageVersionsCentrally`・`CentralPackageTransitivePinningEnabled`）は変えない。

### 決定 2 — CI とイメージのビルドは locked mode で restore する

- `ci.yml`（脚・`backend-verify-build`・submodule の脚）・`integration.yml`・`security.yml` の `dotnet restore` と、2 ユニットの Dockerfile（15 本）の
  `dotnet restore` に `--locked-mode` を付ける。lock と食い違えば NU1004、中身が違えば NU1403 で落ちる（実測）。
- locked mode は lock の無いプロジェクトでは落ちずに lock を新しく作る（実測）。`backend-verify-build` は restore の後に lock ファイルの未追跡・変更を
  `git status` で見て落とす。`scripts.repo.test.js` は全 `.csproj` の隣に追跡下の lock があることを見る。
- 暗黙の restore（`dotnet format`・CodeQL の `dotnet build`）へは足さない。lock が在れば lock の版で解決し、食い違いは `backend-verify-build` が落とす。
- MSBuild の `RestoreLockedMode` を CI の環境変数で一律に付ける形は採らない。CI で動く AI のコーディング（`claude-coding.yml`）がパッケージを足すとき、
  lock を書き直す restore まで止まるため。

### 決定 3 — PR の CI へ計画リポジトリを読むトークンを渡さない

- `ci.yml` の突合のステップの env を `${{ github.event_name == 'push' && secrets.PLANNING_REPO_TOKEN || '' }}` にする。PR では空になり、検査器は既存の経路で
  `unverified`（`scanned: 0`・exit 0）を返す。push（develop / main）の CI と週次の `backlog-audit.yml` は従来どおり突合する。
- ステップ・ジョブ・`on:` は変えない（必須チェックの名前と起動条件を保つ）。

## 結果

- 推移的な依存の版と中身が CI とイメージのビルドで固定される。パッケージを足す・上げる PR は、lock の差分を同じ PR に含めることになる。
- PR の時点の計画 ID レンジのずれは PR では見えなくなる（push の CI と週次の報告が拾う。突合はもともと警告だけで、PR を止めていない）。

## 残る制約

1. 同じリポジトリのブランチの PR は、ワークフローの本文を変えれば repo の secret を読める（GitHub の仕様）。決定 3 は既定の経路から外すだけである。
   トークンの権限を計画リポジトリの Contents の読み取りだけ・期限つきに絞る作業はオーナーが行う（`docs/operations/planning-repo-token-runbook.md`）。
2. Platform.Bff は AST の Bff を参照するため、submodule の bump で AST の依存が変われば Platform.Bff の lock の書き直しが要る。
3. Dependabot の NuGet の更新が lock を書き直さない場合、その PR は CI が赤になる（黙って通らない側）。
