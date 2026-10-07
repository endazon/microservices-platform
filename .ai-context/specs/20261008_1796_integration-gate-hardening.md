---
title: 作業仕様書 — 統合試験の DockerRequired の CI=true 近道を外し、--list-tests の部分失敗を門で捕まえる（#1796）
type: spec
status: done
related_ids: [NFR, ADR-0007, ADR-0090, IADR-0232, IADR-0414, IADR-0507]
author: claude
created: 2026-10-08
updated: 2026-10-08
issue: "#1796"
---

# 作業仕様書 — 統合試験の門の後続 2 点（#1796。#1788 の後続）

> 本仕様書は着手時（2026-10-08）に母集合の走査から起こし、実装と同じ PR に置く。基点は MSP `origin/develop` `3396657b`。
> 前提: #1788（PR #1794・IADR-0507）と、その独立監査の 🟡 2 件（PR #1794 のコメント「独立監査の記録」）。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0090 決定 1・3、フォローアップ 4**（planning#575）／ADR-0007（CI）。
- 非機能要件: 無採番（CI の門。メタ作業。#1788 と同じ扱い）。
- 新しい IADR は起こさない。決定は IADR-0414（近道の撤去）と IADR-0507（一覧の部分失敗）への日付つき追記で残す（どちらも既存の決定の適用・拡張であり、新しい選択肢の比較を要しない）。

## 受け入れ基準

- AC1: **Given** CI（`CI=true`）で Docker 等の依存が得られない **When** 統合試験を走らせる **Then** 依存を要る試験は skip し、#1788 の門が「依存を得られない skip N 件」で失敗させる。`DockerRequired` の `CI=true` 近道を外し、IADR-0414 へ日付つきで追記する。依存が揃った CI 実行は従来どおり緑（workflow_dispatch で実測）。
- AC2: **Given** あるユニットの `--list-tests` が失敗（非 0 終了、または見出しはあるが名前 0 件で TRX にそのユニットの統合試験の結果がある） **When** 門を走らせる **Then** そのユニットを理由つきで失敗させる。ユニットごとの終了状態を記録し、検査器が読む。自己試験に監査の実験の入力を加える。
- AC3（否定形）: 設計どおりの条件つき skip（外部ブローカの構成時だけ走る試験）と、宣言 0 件で全プロジェクトが「該当なし」を返すユニット（platform）では失敗させない。
- AC4: `docs/ai-workflow.md` の `integration` の行に、全 skip・依存不足 skip で失敗することを書き足す。
- AC5: 変異を殺す試験を足す（.NET 側: 近道の再混入／検査器側: 新しい判定の各枝）。既存の陽性・陰性の試験は残す。

## 母集合（規則 9・10。`3396657b` 時点）

### (1) `CI=true` 近道・`DockerRequired` の使われ方

走査: `git grep -n '"CI"' -- src ':!src/ai-stock-trading'` → **1 件**（`DockerRequired.cs:26`。これが近道）。
`git grep -n DockerRequired -- src ':!src/ai-stock-trading'`:

| 箇所 | 使い方 | 近道を外した影響 |
| --- | --- | --- |
| `RequiredServices.cs:88`（`Obtainable`） | 門。外部供給 or Docker | Docker の無い CI で skip（意図した変化）。ソケットの在るランナーは不変 |
| `BrokerRequired.cs:25` | 門（ブローカ） | 同上 |
| `PostgresFixture.cs:66` / `RabbitMqFixture.cs:69` | `ContainerStartupFailure.ToThrow(…, DockerRequired.IsAvailable())` | Docker の無い CI で起動失敗が throw ではなく skip へ倒れる → 門が skip → 本門が赤。Docker の在る CI は不変 |
| `IngestToSearchQdrantTests.cs:61` / `KeywordIndexQdrantTests.cs:57` / `LexicalIndexQdrantTests.cs:61` | `InitializeAsync` でコンテナを起こさない判定（試験本体は `RequiredServices` の門） | 同上（門で skip） |
| `ContainerStartupFailure.cs` のコメント「CI で無条件に真を返すのは前提」 | 記述 | **誤りになる** → 書き換える（規則 10） |
| `ContainerStartupFailureTests.cs` のコメント | 記述（ガードを置かない理由） | 影響なし |
| `.ai-context/adr/IADR-0414` / `IADR-0507`（実測 6・残余リスク） | 凍結記録 | 本文は変えず日付つき追記 |
| `.ai-context/specs/20260908_issue-1292_*` ほか確定済み仕様書 | 凍結記録 | 書き換えない |

### (2) `--list-tests` の出力の消費者

走査: `git grep -n -e integration-lists -e list-tests -e check-integration-executed`:

| 箇所 | 扱い |
| --- | --- |
| `.github/workflows/integration.yml` 試験 step（一覧の生成。終了コードを `|| echo ::warning` で捨てている） | **変更**: 終了コードを `<unit>.exit` へ残す |
| `scripts/check-integration-executed.js`（唯一の読み手） | **変更**: G6（部分失敗）を足す |
| `scripts/scripts.repo.test.js`（配線の固定） | **変更**: `.exit` の配線と「終了コードを捨てる形」の不在を固定 |
| `scripts/README.md`・`docs/ai-workflow.md` | 記述を足す |
| `IADR-0507`・`20261008_1788` 仕様書 | 凍結。IADR は日付つき追記 |

一覧の実出力（2026-10-08 実測。`dotnet test <slnx> --no-build -c Release --list-tests --filter "Category=Integration"`）:
knowledge は 12 プロジェクトすべてが `Test run for <dll> (…)` と見出しを出し、宣言の無い 11 プロジェクトは `No test matches … in <dll>` を出し、`Knowledge.IntegrationTests` だけが名前 59 件を出す。platform は 7 プロジェクトすべてが見出しと `No test matches` を出す（宣言 0）。終了コードはどちらも 0。

## 設計

1. `DockerRequired.IsAvailable()` から `CI=true` の分岐を外す。判定を `IsAvailable(getEnv, probeDefaultEndpoint)` へ切り出し、既定は従来の `DOCKER_HOST`・パイプ／ソケット。`DockerRequiredTests`（`TestKind=Unit`）で「`CI=true` だけでは false」「既定の端点が在れば true（CI の有無によらず）」「`DOCKER_HOST` で true」「空の `DOCKER_HOST` は未設定」を固定する。
2. `integration.yml`: `list_rc=0; dotnet test … > "$lists/$unit.list" 2>&1 || list_rc=$?; echo "$list_rc" > "$lists/$unit.exit"`。
3. `check-integration-executed.js` に G6（ユニットごと）: (a) `.exit` が無い・数値でない・0 でない (b) 見出し数 < `Test run for` 数 (c) 宣言 0 件で、`Test run for` のうち `No test matches … in <dll>` を返さなかったプロジェクトがある (d) TRX に一覧外の「依存を得られない」skip がある。summary の表に「一覧の終了」列を足す。
4. 文書: `docs/ai-workflow.md`（integration の行・配備済みの手段）、`scripts/README.md`、IADR-0414・IADR-0507 への追記。

## 検証（証跡）

- 検査器の自己試験: 20 → **29 件 OK**。変異（10 本すべて killed）: 記録なしの判定を外す／非 0 の判定を外す／見出し数の判定を外す／「該当なし」突合を外す／一覧外の依存不足 skip の判定を外す／空の記録を 0 と読む／記録の欠落を 0 と読む／G6 全体を外す／`No test matches` の dll の取り出しを壊す／`Test run for` の dll の取り出しを壊す。
- .NET: `DockerRequiredTests` 5 件合格。近道を戻す変異で `CiTrue_WithoutAnyDockerEndpoint_IsNotAvailable` が失敗（killed）。
- 実データ（AC1）: daemon に届かない環境で `CI=true dotnet test Knowledge.IntegrationTests --logger trx`。近道ありでは **Failed 59**、外した後に `/var/run` を tmpfs で隠すと **Passed 73 / Skipped 58**。検査器（実一覧＋TRX）は `| knowledge | 59 | 1 | 1 | 0 | 58 | 57 | 0 | 0 | 1 | 0 |` で **exit 1**（依存を得られない skip 57 件）。G6 は実一覧で発火しない（否定形）。
- 実データ（AC2）: 実一覧から名前行を抜いた入力（監査の実験の形）で G6 が「該当なしも返さなかった試験プロジェクト」「一覧に無い試験が依存不足で skip（57 件）」を挙げて exit 1。platform の実一覧（7 プロジェクト・宣言 0）は G6 で発火しない。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、`actionlint`、文書系検査は PR に記す。
- 依存が揃った実行での緑（AC1 後半）は PR の枝で `workflow_dispatch` を起こして確かめる（結果は PR に書く）。
