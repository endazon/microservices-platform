---
title: 作業仕様書 — #1040 ビルダ構築時に読まれる構成キーの静的検査（同型の事故 3 回目）
type: spec
status: done
related_ids:
  - NFR
  - UC-03
  - UC-04
  - UC-05
  - ADR-0027
author: claude
created: 2026-08-28
updated: 2026-08-28
related_adrs:
  - IADR-0293
  - IADR-0232
  - IADR-0286
  - IADR-0291
---

# 作業仕様書 — #1040 ビルダ構築時に読まれる構成キーの静的検査

## 目的

**同じ罠を 3 回踏んだ**（`Pipeline:ConfigPath` / `RabbitMq:ConnectionString` / `ConnectionStrings:DefaultConnection`）。規約「同型の事故が 2 回起きたら検査器を置く」の条件を超えているため、散文の警告ではなく機械で止める。

`integration.yml` は develop への push と日次でしか起動しない（[[IADR-0232]] 決定 1）ため、**この型の欠陥を PR で捕まえる手段が現状ゼロ**である。静的走査なら PR で走らせられる。

## 母集合の引き方と、引いた結果

**着手前に自分で引いた**（`.claude/rules/traceability.repo.md`「是正・追随の母集合の取り方」）。

### 軸 1: 構成を読む側（`Program.cs`）

`find src/knowledge/backend/Services src/platform/backend -name Program.cs` = **15 本**。
🔴 **`src/knowledge` だけで引くと AuthorizationService を落とす** —— 同サービスは `src/platform/backend/Services/` に在る（波 4.5 の移送後）。1 軸で終わらせない（規則 5）。

`Build()` 前に読まれる構成キーを実測したところ、**既定値つきの読みが 8 件**あった（`Services:LlmGateway` / `WikiJs:ApiKey` / `WikiJs:GraphQlEndpoint` / `Qdrant:Host` / `Qdrant:Port` / `Services:NotificationService` / `Services:AuthorizationService` ほか）。**これらを要求すると偽陽性の山になる。**

### 軸 2: 構成を与える側（器）

`grep -rn 'UseSetting' src/knowledge/backend/Tests/` = 実コード 6 箇所（残りはコメント）。**3 ファイルに散っている**:

- `Fixtures/IntegrationTestFactory.cs`（基底。3 キー）
- `Messaging/QueueOverrideFanOutTests.cs`（`Pipeline:ConfigPath` を上書き）
- `McpTools/McpToolDeclarationHosts.cs`（単独の器。2 キー）

**この 2 軸目が決定的だった**（後述「破れた予測」）。

### 除外したものと理由（規則 6）

| 除外 | 理由 |
| --- | --- |
| 器を持たないサービス（RetrievalService / FeedbackService / DashboardService / NotificationService / McpServer / LlmGateway / Platform.Bff） | **要求する器が無いキーを要求しても意味が無い。** `TestMarker.cs` の有無で機械的に判定する |
| 既定値つきの読み（8 件） | 未注入でも壊れない。[[IADR-0293]] 決定 1 |
| コメント行に書かれた読み方 | 器も `Program.cs` もこの罠を散文で説明しており、拾うと自分の警告文で落ちる |

## 🔴 破れた予測 —— 初版は緑だったが検出力ゼロだった

初版は**テスト木全体の `UseSetting` を 1 つの集合**に集めた。実データでは緑になった。

**そこで変異試験を回したところ、基底から 3 件のキーを 1 つずつ外す変異が 3 件とも生存した。** 同じキーを `QueueOverrideFanOutTests` と `McpToolDeclarationHosts` も与えているため、集合から消えなかったからである。

**「実データで緑」は「検出力がある」を意味しない。** 変異試験を回さずに着地させていれば、検出力ゼロの検査器を「置いた」と記録して終わっていた。器（host group）ごとに数える形へ作り直した（[[IADR-0293]] 決定 2）。

## 受け入れ基準と実測

issue #1040 が挙げた 5 条件をすべて実測した。

| # | 受け入れ基準 | 実測 |
| --- | --- | --- |
| 1 | `UseSetting("ConnectionStrings:DefaultConnection")` を消すと落ちる | **KILLED** |
| 2 | `UseSetting("RabbitMq:ConnectionString")` を消すと落ちる | **KILLED** |
| 3 | `UseSetting("Pipeline:ConfigPath")` を消すと落ちる | **KILLED** |
| 4 | `ConfigureAppConfiguration` にだけ在る状態で緑にならない（最重要） | **self-test で固定**（専用ケース） |
| 5 | 現状の develop で緑（既存の正しい配線を誤検知しない） | **緑**（器 × サービスの組 14 件・違反 0） |

追加で、単独の器（`McpToolDeclarationHosts`）から `ConnectionStrings:DefaultConnection` を外す変異も **KILLED**。

**1〜3 の変異試験は `scripts.repo.test.js` に入れた** —— 検査器が将来「緑だが検出力ゼロ」へ退行したら、そこで落ちる。

## 成果物

- `scripts/check-integration-config-timing.js`（`--self-test` 8 件）
- `.github/workflows/ci.yml` の `static-checks` へ self-test ＋ 本走査を配線
- `scripts/README.md` の一覧へ 1 行
- `scripts/scripts.repo.test.js` へ 7 件（検出力の実測を含む）／検査器の母集合ラチェット 44 → 45
- [[IADR-0293]]

## 検証

- `node scripts/check-integration-config-timing.js --self-test` → 8 件通過
- `node scripts/check-integration-config-timing.js` → 器 × サービスの組 14 件・違反 0
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → **652 件緑**（645 → +7）
- 検査器一式（`check-default-credentials` / `check-trace-blocks` / `check-doc-updated` / `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-reading-budget` / `check-route-manifest` / `gen-knowledge-graph --check`）→ すべて OK
- `ci.yml` の YAML 解析 → ジョブ 10・起動条件不変・`static-checks` のステップ 42
