---
title: テスト戦略（退行防止テスト基盤）
type: test-spec
status: in-progress
related_ids:
  - NFR
  - IADR-0034
  - IADR-0115
  - IADR-0116
author: Claude
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# テスト戦略 — 再実装の退行防止基盤

> リポジトリ単位の横断ドキュメント。個別の FR/SC のテスト仕様書は同ディレクトリの
> `FR-xx_*.md` / `SC-xx_*.md` に置く。作業仕様書:
> [20260803_issue-453_regression-test-foundation.md](../specs/20260803_issue-453_regression-test-foundation.md)

## なぜ要るか

全面再実装（#454）では**既存実装を破棄し得る**。コードが入れ替わるため、退行の検知手段をコードでは
なく**テストへ移す**必要がある。#453 は各ドメイン issue（#438〜#452）のテストが載る共通基盤と横断
ルールを、他のすべてに先立って整備する。

## 受け入れ基準 → テストの写像規約

計画書（`02_requirements` / `03_usecases` / `05_screens`）の受け入れ基準を、**テストの直前のコメントに
起点 ID を書く**ことで突合可能にする。

```csharp
// FR-03, UC-01: ハイブリッド検索は語彙一致とベクトル類似の両方を返す
[Fact]
public async Task 検索は語彙一致とベクトル類似の両方を返す() { ... }
```

```ts
// SC-02: 検索結果一覧は 0 件のとき空状態を表示する
it('0 件のとき空状態を表示する', () => { ... })
```

### なぜテスト名ではなくコメントか

テスト名に ID を埋める規約（`FR03_...`）は、**日本語のテスト名**という本リポジトリの既存慣習と両立
しない。また ID が変わるたびにテスト名が変わり、履歴の追跡が切れる。コメントなら
[`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)「テスト: テスト名またはコメントに
起点 ID を残す」の既存規約にそのまま乗る。

### 規約

- テストメソッド / `it` / `test` の**直前**のコメントに起点 ID を 1 つ以上書く（`FR-\d+` / `UC-\d+` / `SC-\d+` / `NFR`）。
- 複数 ID はカンマ区切り（`// FR-03, UC-01: ...`）。
- 他プロジェクトの ID は修飾する（`AST/FR-17`）。**修飾付き ID は本リポジトリの突合対象から除外される**。
- **起点 ID を持たないテストを禁止しない。** 基盤・回帰・検査器自身のテストは計画 ID に紐づかない。
  検査が見るのは「仕様書がある FR/SC にテストが 1 件も無い」ことだけである。

## ゲート一覧

| ゲート | 対象 | 実行 | 判定 |
| --- | --- | --- | --- |
| **写像検査** | `docs/tests/` の FR/SC ↔ `src/` のテスト | [`check-test-traceability.js`](../../scripts/check-test-traceability.js) | allowlist に無い未写像 → **fail**。allowlist 内 → warn。写像済みなのに allowlist 残置 → **fail** |
| **バックエンド カバレッジ床** | `src/*/backend/**` | [`check-coverage-floor.js`](../../scripts/check-coverage-floor.js) ＋ `ci.yml` | `src/coverage-floor.json` の床未満 → **fail** |
| **フロント カバレッジ ratchet** | `src/*/frontend/**` | [`frontend-tests.yml`](../../.github/workflows/frontend-tests.yml) | [`src/vitest.config.ts`](../../src/vitest.config.ts) の `thresholds` 未満 → **fail**（[IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md)） |
| **ユニット依存規則** | `.csproj` の `ProjectReference` ・Foundation→Composable | [`check-unit-dependencies.js`](../../scripts/check-unit-dependencies.js) | 違反 → **fail** |
| **BFF 境界** | BFF の downstream | [`check-bff-downstreams.js`](../../scripts/check-bff-downstreams.js) | 違反 → **fail** |
| **ライブラリ標準（ADR-0030）** | `PackageReference` / `using` ・Domain 層の依存 | [`check-backend-libraries.js`](../../scripts/check-backend-libraries.js) | 新規混入・baseline 減らし忘れ → **fail**（#455） |

### 共通する設計原則: ratchet

上記のうち写像検査・カバレッジ床・ライブラリ標準はいずれも **ratchet**（床は下げられるが上げっぱなしに
できない）で設計している。これは [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md) が
planning#162 を引いて警告した「**成果物は正しいのに赤**」の常態化を避けるためである。既知の残件を
明示（allowlist / baseline / floor）したうえで、**新規の悪化だけを止める**。あわせて「残件が消えたのに
明示が残っている」ことも fail にする。これが無いと残件表が減らないまま形骸化する。

## テスト種別と責務

| 種別 | 置き場所 | 使うもの | 責務 |
| --- | --- | --- | --- |
| 単体（バックエンド） | `Services/<Name>/tests/<Name>.UnitTests` | xUnit v3 ＋ AwesomeAssertions ＋ NSubstitute（ADR-0030） | ドメイン規則・ハンドラの分岐 |
| 統合（バックエンド） | `Services/<Name>/tests/<Name>.IntegrationTests` | Testcontainers（PostgreSQL / RabbitMQ / Redis / Qdrant）＋ Respawn ＋ `Mvc.Testing` | 実依存を伴う往復・イベント連鎖 |
| 単体（フロント） | 実装と同居（`*.test.tsx`） | Vitest（jsdom）＋ Testing Library | 画面要素・状態遷移 |
| E2E | `src/*/frontend/e2e` | Playwright | 主要導線（**統合スタックでの拡充は後続 issue**） |
| 契約 | — | **未整備（後続 issue）** | `Shared.Contracts` の後方互換 |
| 性能（NFR） | [`NFR-01_performance-load-test.md`](NFR-01_performance-load-test.md) | — | 検索 p95 1.5s / RAG 初回 5s / 取り込み 1 万件・時（[#196](https://github.com/endazon/microservices-platform/issues/196)） |

## 本基盤の未整備部分（後続 issue へ切り出し）

#453 のスコープのうち、以下は独立した設計判断を伴うため別 issue とする
（[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4: 大きくなる場合は
PR ではなく issue を分割する）。

| 項目 | 切り出す理由 |
| --- | --- |
| 契約テスト基盤（`Shared.Contracts` のスキーマ後方互換） | 抽出方式（リフレクション / OpenAPI / proto）の選定から要り、[IADR-0049](../adr/IADR-0049_composability-standards-phased-adoption.md) の繰延判断の見直しを伴う |
| E2E スモークセット（Istio・Keycloak・BFF の統合スタック） | 実行環境の CI 上での起こし方が主題であり #442（エッジ・実行基盤）と密結合する |
| NFR 性能試験の枠組み | [#196](https://github.com/endazon/microservices-platform/issues/196) が担当。再実装後の受け入れゲートとして接続するのは各サービス完成後 |
| CPM バージョン直書き禁止の機械検査 | `.csproj` の `PackageReference` に `Version` を書かない規約（CLAUDE.md）の機械化。単独で小さく、他の検査器と関心が異なる |

## 各ドメイン issue が守ること

1. 実装する FR/UC/SC の**受け入れ基準をテストへ写像**し、テストの直前コメントに起点 ID を書く。
2. カバレッジ床を下回らない。テストを増やしたら**床を引き上げる**（`src/coverage-floor.json` /
   `src/vitest.config.ts`）。
3. ADR-0030 の不採用ライブラリを増やさない。移行したら `scripts/backend-library-baseline.json` から
   自プロジェクトを削除する。
4. 写像を後回しにする場合は `scripts/test-traceability-allowlist.json` へ**理由とともに**追加し、
   テストを書いた PR で削除する。
