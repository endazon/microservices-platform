---
title: 作業仕様書 — Knowledge.IntegrationTests を xUnit1051 から剥がす（実測 4 箇所）（#882）
type: spec
status: done
related_ids:
  - NFR
  - FR-04
  - FR-07
  - ADR-0030
  - IADR-0238
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (テスト = xUnit v3)
related_specs:
  - "../adr/IADR-0238_xunit1051-staged-adoption-ratchet.md"
  - "20260822_issue-882_xunit1051-staged-adoption-harness.md"
issue: "#882"
---

# 作業仕様書 — Knowledge.IntegrationTests を xUnit1051 から剥がす

## 目的と射程

実測の昇順で 2 番目（**4 箇所**）。`Refs #882`（`Closes` は最後の 1 本だけ）。

🔴 **本プロジェクトは「推定が最も外れた」実例である。** 着手時の推定は **~255 件**（13 件中 12 位）
だったが、**実測は 4 件**（1 位）だった。**60 倍以上の乖離**であり、`await` の出現数を代理指標に
した外挿がプロジェクト単位ではまったく当てにならないことを示す
（詳細は [`20260822_issue-882_dashboardservice-xunit1051.md`](20260822_issue-882_dashboardservice-xunit1051.md)）。

### 起点 ID の置き方

`related_ids` の `FR-04` / `FR-07` は「**移行対象のテストが何を検証しているか**」という文脈であり
（`RagOrchestratorTests.cs` の先頭コメントが名乗っている）、**本 PR が実装したものではない**。
件名のスコープを無採番 `NFR` ＋ `IADR-0238` にしている理由は
[`20260822_issue-882_dashboardservice-xunit1051.md`](20260822_issue-882_dashboardservice-xunit1051.md)
の「起点 ID の置き方」節に書いた（**同じ判断を各仕様書へ複写しない**）。

## 対象の母集合（走査で引いた）

`dotnet build src/knowledge/backend/backend.slnx -t:Rebuild -p:NoWarn= -m:1` の出力を
ファイル・行・列で一意化して引いた。**4 箇所・1 ファイル。**

| ファイル | 行 | 呼び出し |
| --- | ---: | --- |
| `AiAnalysisService/RagOrchestratorTests.cs` | 33 | `PostAsJsonAsync("/analysis/ask", …)` |
| 同上 | 38 | `ReadFromJsonAsync<AiAnswerDto>()` |
| 同上 | 47 | `PostAsJsonAsync("/analysis/analyze", …)` |
| 同上 | 55 | `ReadFromJsonAsync<AiAnswerDto>()` |

**同プロジェクトの他ファイルは診断 0 件である。** 統合テストは `await` を多く持つが、
その多くは Testcontainers の起動・破棄や `IAsyncLifetime` であり、
**`CancellationToken` を受けるオーバーロードを持つ呼び出しではない**。これが推定を外した原因である。

`using` の追加は不要 —— `.csproj` が `<Using Include="Xunit" />` を持つ
（同プロジェクトには手書きの `GlobalUsings.cs` が無く、csproj の `Using` 項目で入れている）。

🔴 **2 箇所は同一テキスト**（`ReadFromJsonAsync<AiAnswerDto>()`）である。置換前に出現件数を assert する。

## 走る経路（PR CI で検証されるか）

本ファイルは **`[Trait("Category", "EndpointRouting")]`** を持つ。
`ci.yml` の `backend-build` は `--filter "Category!=Integration"` で**Testcontainers を使う
`Category=Integration` だけを除外**するため、**本ファイルは PR CI で実走する**
（`IADR-0232` 決定 3）。Docker を要さないインプロセステストである。

## 手順（器が強制する 3 点セット）

1. テストの `.cs` を直す（4 箇所）
2. `scripts/xunit1051-baseline.json` を `remaining: 0` / `migrated: true`
3. `src/Directory.Build.props` の `XUnit1051Migrated` へ追加

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| 4 箇所が移行済み | ✅ `-p:NoWarn=` 付き再測定で**一覧から消えた**。knowledge 合計 **502 → 498**（ちょうど −4） |
| **再発したらビルドが落ちる** | ✅ 変異試験 M-1（`error xUnit1051` / exit 1） |
| **テスト件数が減らない** | ✅ **29 → 29**（`[Fact]`/`[Theory]` 数も develop 版と一致） |
| 他プロジェクトの残件が変わらない | ✅ 他 8 プロジェクトの実測が baseline と一致 |
| 器の 3 点が揃っている | ✅ `check-xunit1051-ratchet.js` exit 0 |

### 変異試験

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-1 | 移行済みの 1 箇所を元へ戻す | **ビルド失敗** | **`error xUnit1051` / exit 1** |
| M-2 | baseline を `migrated:true` のまま `remaining: 4` へ戻す | `migrated-nonzero` | **exit 1・当該 1 件のみ** |
| M-3 | props の許可リストから外す | `props-desync` | **exit 1・当該 1 件のみ** |

復旧は `cmp` でバイト一致を確認した。

## 申し送り

残件 **919 → 915**。移行済み 5 プロジェクト。
次は実測の昇順で `AiAnalysisService.Api.Tests`（30）／ `FeedbackService.Api.Tests`（30）。
