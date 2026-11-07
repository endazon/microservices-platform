---
title: 作業仕様書 — NuGet の推移的な依存を lock ファイルで固定し、PR の CI へ計画リポジトリを読むトークンを渡さない
type: spec
status: done
related_ids: [NFR, IADR-0701, IADR-0232, IADR-0423]
author: claude
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# 作業仕様書 — NuGet の lock ファイルと PR の CI のトークン

> 本仕様書は、変更の前に作成した（着手 2026-11-07。develop `cc8f316f1`）。実測は同じ日の手元の作業である。

## 起点となる計画書（トレーサビリティ）

- 非機能要件: NFR（供給網と CI の統制。該当する採番が無いため無採番）。
- 機能要求（FR）・ユースケース（UC）・画面（SC）: なし。
- 関連 IADR: IADR-0232（CI の構成・NuGet のキャッシュ）、IADR-0423（計画 ID レンジの突合）。本作業の決定は IADR-0701。

## 目的・背景

1. 直接の依存は Central Package Management（`src/Directory.Packages.props`）で固定しているが、推移的な依存の解決結果（版と content hash）は
   固定していなかった。追跡下に `packages.lock.json` が 0 件で、イメージのビルド（各 Dockerfile の restore）も同じだった。
2. `ci.yml` は `pull_request` でも動き、`static-checks` ジョブの計画 ID レンジの突合のステップへ `PLANNING_REPO_TOKEN` を渡していた。

## 対象範囲

- `src/Directory.Build.props`（lock ファイルを 2 ユニットに限って有効にする）。
- `src/platform/backend/**`・`src/knowledge/backend/**` の `packages.lock.json`（`dotnet restore` で生成。手で書かない）。
- `.github/workflows/ci.yml`・`integration.yml`・`security.yml` の `dotnet restore` に `--locked-mode`。`ci.yml` の `backend-verify-build` に lock の差分の検査。
- 2 ユニットの Dockerfile（15 本）の `dotnet restore` に `--locked-mode`。
- `ci.yml` の計画 ID レンジの突合のステップのトークンを push のときだけにする。
- 対象外: AST（`src/ai-stock-trading`）のバックエンド（AST 側の別作業）。雛形（`templates/`）。`copilot-setup-steps.yml`（エージェントの環境の準備で、CI の検証ではない）。

## 設計

- `RestorePackagesWithLockFile` は `src/Directory.Build.props` からの相対パスが `platform/backend/` か `knowledge/backend/` で始まるプロジェクトだけに付ける。
  本ファイルは import-chain で AST と雛形の試しビルド（`src/.template-buildcheck-*`）にも届くため、条件なしにはしない。
- CPM の方式（`ManagePackageVersionsCentrally`・`CentralPackageTransitivePinningEnabled`）は変えない。
- CI は明示の `dotnet restore … --locked-mode`。locked mode は lock の無いプロジェクトでは落ちずに新しく作るため（実測）、`backend-verify-build` で
  restore の後に lock ファイルの未追跡・変更を `git status` で見て落とす。
- トークンは `${{ github.event_name == 'push' && secrets.PLANNING_REPO_TOKEN || '' }}`。PR では空になり、検査器は既存の経路で `unverified`（exit 0）を返す。
  ステップ・ジョブ・`on:` は変えない（必須チェックの名前と起動条件を保つ）。

## 受け入れ基準

- AC1: 2 ユニットの全 `.csproj`（39 本）が隣に追跡下の `packages.lock.json` を持ち、ユニットの外（AST・雛形）に lock を作らない。
- AC2: `dotnet restore <slnx> --locked-mode` が platform・knowledge・AST の 3 つの slnx で通る。CPM の版を lock と食い違わせると NU1004、
  lock の content hash を変えると NU1403 で落ちる。
- AC3: `ci.yml`・`integration.yml`・`security.yml` と 2 ユニットの Dockerfile の `dotnet restore` がすべて `--locked-mode` を付ける。
- AC4: `backend-verify-build` が restore の後に lock の差分を見て、あれば落とす。
- AC5: `ci.yml` の `PLANNING_REPO_TOKEN` は push のときだけ渡る。`on:` に `pull_request_target` が無い。必須チェックの名前は変わらない。
- 各 AC は `scripts/scripts.repo.test.js` の IADR-0701 の節で固定し、陰性対照（lock を外す・フラグを外す・条件なしで有効・差分の検査を外す・
  条件なしでトークンを渡す）で赤になることを確かめる。

## 母集合（規則 9・10）

- `dotnet restore` の呼び出し: `git grep -n "dotnet restore" -- .github scripts src/platform src/knowledge`。ワークフローの明示の restore は
  ci.yml 3・integration.yml 1・security.yml 1・copilot-setup-steps.yml 1（対象外、理由は上）。Dockerfile は 15 本。
  暗黙の restore（`backend-format` の `dotnet format`・`codeql.yml` の `dotnet build`）は lock が在れば lock の版で解決する（lock と食い違うときの検出は
  `backend-verify-build` が持つ）。
- 既存の試験の追随: `scripts.repo.test.js` の `backend-verify-build` と脚の restore の形の 3 か所（`--locked-mode` を足した形へ）。
- 新たに誤りになる自分の記述: `ci.yml` の NuGet キャッシュのコメント（「packages.lock.json を持たない」）を過去形へ直した。
- `PLANNING_REPO_TOKEN` の参照: ci.yml 1（本作業）・backlog-audit.yml 1（`schedule`・`workflow_dispatch` だけで PR では動かない。変えない）。

## 実測（2026-11-07・SDK 10.0.403）

| 操作 | 結果 |
| --- | --- |
| `dotnet restore` を 2 ユニットの slnx へ | lock が 39 本できた（AST の配下には 0 本） |
| `--locked-mode` を platform・knowledge・AST の slnx へ | 3 つとも exit 0 |
| CPM の版を 1 つ下げて `--locked-mode` | NU1004・exit 1 |
| lock の content hash を変えて（obj を消して）`--locked-mode` | NU1403・exit 1 |
| lock を消して（obj を消して）`--locked-mode` | exit 0 で lock を新しく作る → 差分の検査で塞ぐ |

## 残余リスク

- 同じリポジトリのブランチの PR は、ワークフローの本文そのものを変えれば repo の secret を読める（GitHub の仕様）。本作業は既定の経路から
  トークンを外すだけで、この経路は塞がない。トークンの権限を計画リポジトリの読み取りだけ・期限つきに絞るのはオーナーの作業
  （`docs/operations/planning-repo-token-runbook.md`）。
- Platform.Bff は AST の Bff を参照するため、submodule の bump で AST の依存が変われば Platform.Bff の lock の書き直しが要る（`backend-verify-build` が落として知らせる）。
- Dependabot の NuGet の更新が lock を書き直さなければ、その PR は CI が赤になる（黙って通らない側）。

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、文書の検査、`dotnet build` / `dotnet test` / `dotnet format --verify-no-changes`（2 ユニット）。
