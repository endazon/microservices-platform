---
title: 作業仕様書 — #1483 submodule src/ai-stock-trading を AST#1012 を含む develop へ進め、市場の開場中だけ Integration が赤になる形を回収する
type: spec
status: done
related_ids: [NFR, FR-14, IADR-0120, IADR-0441]
author: Claude Opus 5.5 (worker)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
related_specs:
  - 20260925_1489_ast-submodule-bump
issue: "1483"
---

# 作業仕様書 — AST submodule の前進（AST#1012 の取り込み）

## 起点

- issue #1483「[CI] Integration が失敗している」（自動起票。同じワークフローの失敗はコメントで積まれる。
  `ci-failure-issue.yml` は自動では閉じない）。
- NFR は無採番（submodule の前進はメタ作業。#1439 / #1443 / #1492 と同じ扱い）。本リポジトリからは AST を直さない（IADR-0120）。

## #1483 に積まれた失敗の分解（`gh run view --log-failed`）

| # | 形 | 代表 run | 状態 |
| --- | --- | --- | --- |
| ① | `Knowledge.IntegrationTests.Storage.ObjectStorageRoundTripTests` 3 件が Testcontainers の pull で `unauthorized`（MinIO のイメージが匿名取得で 401） | 36124424013 〜 36166083523 | #1513（`8b66e42a`。SeaweedFS へ差し替え）で解消。以後の run では 3 件とも Passed（例: 36184252488） |
| ② | AST の `TradeExecutionPipelineE2ETests` が null（#1484 の dependabot bump 以降） | 36124424013 ほか | #1492（`ae8691c9`。AST `471cbf31` へ前進）で解消。36177647836 で 2 件とも Passed |
| ③ | AST の `MarketMonitorService.Tests.RiskManagementGrpcTests.T_10_1057_Grpc_を宣言すれば本番の組み立てが_gRPC_実装を選び実際に提供側を呼ぶ` が `Expected host.Behavior.Calls to be 1, but found 2.` | 36170016883 / 36171862860 / 36172749771 / 36174280834 / 36174317725 / 36177647836（いずれも AST `471cbf31`） | **未回収。本仕様書** |

### ③ の原因

- 確率ではなく**壁時計の時刻で**落ちる。試験のホストが本物の `Program.cs` を起こし、常駐の巡回
  `MonitorPollingService` を外していなかった。巡回は起動直後に 1 回回り、どれかの市場が開いていれば
  `IPositionStore.GetOpenPositionsAsync` を呼ぶので、偽の提供側への呼び出しが 2 回になる
  （米国 13:30 UTC〜・日本 00:00 UTC〜。AST#1012 の実測）。
- AST 側は AST#1012（`9e133b55`。AST#1010 を閉じた）で試験のホストから巡回の登録を外して是正済み。
- 本リポジトリの pin `471cbf31` は AST#1012 を含まない（`git merge-base --is-ancestor 9e133b55 471cbf31` が偽。実測）。
- 同じ pin `471cbf31` のまま 20:11 UTC 以降の run（36184252488 ほか）が緑なのは**米国の閉場後と週末だから**で、
  直ったからではない。**次に市場が開けば（月曜 00:00 UTC の東京）再び赤になる。**

## 取り込む SHA

AST develop の先端 **`7a7a8a149949a6ff24fd9501db46fbd6104ea4c3`**（AST#1027）。AST#1012 を含む（`9e133b55` の子孫。実測）。
先端の AST の run は CI / Integration E2E（36218416035）/ Helm / OpenAPI / Security / CodeQL がすべて success。
`471cbf31..7a7a8a14` は 15 コミット。

## 対象範囲

- 対象: `src/ai-stock-trading` を `471cbf31` → `7a7a8a14`。
- 対象外（前進で動かないことを確かめたもの）:
  - **フロントエンド**: `git -C src/ai-stock-trading diff --stat 471cbf31 7a7a8a14 -- frontend` が空。合成ビルドの入力
    （`@ai-stock-trading` の別名は `ai-stock-trading/frontend/src` を指す）と `frontend/package.json` が不変なので、
    `pnpm-lock.yaml`・チャンク床（`scripts/chunk-budget-baseline.json`）・Lingui カタログ・`coverage.thresholds` は動かない。
    チャンクの再測は行わない（#1492 の `a35ae3f7` → `471cbf31` と同じ判断）。
  - orval 生成物 / `docs/api/openapi.yaml`（AST を入力にしない）。
  - コードの変更は無い（IADR-0120）。判断を伴う決定は無いので IADR は起こさない。

## 母集合（規則 9・10）

- **submodule の SHA を書く文書**: `git grep -l 471cbf3 -- . ':!src/ai-stock-trading'` で全ファイルを走査した。ヒットは 7 件:
  `scripts/chunk-budget-baseline.json`（`$comment_…_1489_ast-bump`）・`IADR-0458`（表の 1 行「AST submodule `471cbf31`」）・
  `.ai-context/specs/` の 5 本（`20260925_1489_ast-submodule-bump` / `20260925_1397_…` / `20260925_1502_…` /
  `20260925_458_…` / `20260926_1520_…`）。いずれも「その時点の pin で測った」という観察記録で、書き換えない。
  今回の SHA は本書が持つ。
- **submodule を読む検査器**: #1492 の仕様書の母集合と同じ集合を前進後のツリーで実走した（§検証）。

## 受け入れ基準

1. `git -C src/ai-stock-trading rev-parse HEAD` が `7a7a8a149949a6ff24fd9501db46fbd6104ea4c3`。
2. 合成での AST バックエンドの Release ビルドが成功する。
3. `RiskManagementGrpcTests` が合成ビルドで緑（ただし本日は土曜で市場が閉じており、ローカルでは赤の形を再現できない）。
4. submodule を読む静的検査が緑。
5. マージ後、市場の開場中に走る Integration で `T_10_1057_Grpc_を…` が Passed になる（本 PR の CI では示せない）。

## 検証（ローカル。Docker・稼働クラスタは使わない）

| コマンド | 結果 |
| --- | --- |
| `dotnet build src/ai-stock-trading/backend/backend.slnx -c Release` | 成功・エラー 0・警告 1（AST の試験コード `PolicyApprovalRealReportHostTests.cs(194)` の CS0108。AST 側の既存） |
| `dotnet MarketMonitorService.Tests.dll -class MarketMonitorService.Tests.RiskManagementGrpcTests`（`dotnet test` は Windows のパス長 206 で起動できないため xUnit v3 の実行ファイルを直接起動） | Total 13 / Failed 0 |
| check-backend-libraries / check-cpm-versions / check-contract-schema / check-proto-contracts / check-test-traceability / check-realm-constraints / check-realm-copy-drift / check-event-topology / check-secret-injected-options / check-unit-dependencies / check-unit-service-ownership / check-default-credentials / check-bff-downstreams / check-integration-config-timing / check-test-name-references / check-plan-id-qualification / check-route-manifest / check-image-mapping `--require-submodule` / check-cross-repo-refs | すべて EXIT 0 |
| check-deploy-manifests | このホストに kubeconform が無く実行できない（同検査は本リポジトリの chart と overlay を描く。AST の差分は AST の helm values のみ） |

### 検証していないもの

- 本リポジトリの Integration（`integration.yml`）の実走（Docker が要る）。AST の Integration E2E は先端で緑（36218416035）。
- AST のバックエンド全試験の合成での実走（AST の CI が先端で緑。合成で違うのは `Directory.Build.props` の import で、ビルドは通った）。
