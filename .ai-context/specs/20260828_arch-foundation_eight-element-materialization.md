---
title: 作業仕様書 — バックエンド 8 要素標準の実体化（土台＋パイロット FeedbackService）
type: spec
status: done
related_ids:
  - NFR
  - FR-08
  - ADR-0002
  - ADR-0030
  - ADR-0041
  - IADR-0027
  - IADR-0117
  - IADR-0218
  - IADR-0219
  - IADR-0229
  - IADR-0280
author: claude
created: 2026-08-28
updated: 2026-08-28
---

# 作業仕様書: バックエンド 8 要素標準の実体化（土台＋パイロット FeedbackService）

## 背景

オーナー裁定（2026-08-27。planning#490 に環流済み）により、計画
`12_backend-application-stack`（fixed）の 8 要素標準
（`Api`/`Worker`・`Application`・`Domain`・`Infrastructure`・`Contracts`・`SharedKernel`・`Tests`）を
**実プロジェクトとして実体化する**方向が確定した。決定の本体は
[IADR-0280](../adr/IADR-0280_eight-element-standard-materialization.md)（配置写像・参照方向・段階計画）。
本仕様書は段 1（土台＋パイロット）の作業範囲・母集合・受け入れ基準を持つ。

現状（着手時の実測）:

- 全 14 サービス（platform 4 / knowledge 10）が単一 `.Api`（または `.Worker`）プロジェクト。
- `.gitkeep` のみの空枠は `find src -name .gitkeep -path "*backend/Services*"` で **60 件**
  （12 サービス × 5 要素。McpServer / NotificationService は枠ごと無い＝ドリフト）。
- `Platform.Shared.Kernel` は `Result` / `Result<T>` / `Error` の 3 ファイルのみで DDD 基底型が無い。
- `scripts/check-backend-libraries.js` 規則 2（`*.Domain.csproj` は PackageReference を持てない）は
  対象 0 件で空振り。

## 作業範囲（宣言ファイル領域）

1. `.ai-context/adr/`: 新 IADR-0280 ＋ IADR-0027 / IADR-0218 / IADR-0219 への日付つき追記ブロック
   （Amended by）＋ 索引 README の行追加。
2. `src/platform/backend/Shared/Platform.Shared.Kernel/**`: DDD 基底型
   （`Entity<TId>` / `ValueObject` / `AggregateRoot<TId>` / `IDomainEvent`）と
   `Platform.Shared.Kernel.Tests` のユニットテスト。
3. 全 14 サービスの `src/.../Services/<Svc>/src/<Svc>.{Domain,Application,Infrastructure,Contracts}/`
   の実体化（4 要素 × 14 = 56 csproj。既存 `.gitkeep` は該当 4 要素ぶんを削除、
   `SharedKernel` の枠は維持し McpServer / NotificationService へ新設）＋ 両ユニットの `backend.slnx` 登録
   ＋ `<Svc>.{Api,Worker}` への `<Svc>.Infrastructure` 参照追加。
4. `src/knowledge/backend/Services/FeedbackService/**`: パイロット完全移送（振る舞いを変えない）。
5. `scripts/check-unit-dependencies.js` への規則 3 追加と `scripts/scripts.repo.test.js` の自己試験。
6. 実体化により記述が誤りになる live 文書の追随: `src/README.md`・`docs/tech/tech-requirements.md`
   （下の母集合 走査 2）。
7. 本仕様書。

**射程外（後続波）**: 残り 13 サービスの移送・`templates/unit-template/backend/` の追随・
`src/ai-stock-trading` の追随（向こうの issue）。FeedbackService 以外の `*.Api/**` の中身、
`src/coverage-floor.json`、contract-schema baseline は変更しない。

## 母集合の引き方と結果（traceability.repo.md 規則 9・10）

### 走査 1 — `.gitkeep` の枠（実体化で消える・残るものの全数）

```
find src -name .gitkeep -path "*backend/Services*" | wc -l   # → 60
```

60 件 = 12 サービス × 5 要素。うち 4 要素 × 12 = **48 件を削除**（csproj へ置換）、
`SharedKernel` の 12 件は維持 ＋ McpServer / NotificationService へ 2 件新設 → **14 件**。
除外: `templates/unit-template/backend/` の `SampleService.SharedKernel/.gitkeep`（雛形。射程外）、
フロントエンド・`docs/` の `.gitkeep`（本件と無関係の区分枠）。

### 走査 2 — 実体化で誤りになる記述（「`.csproj` は作らない」系）

```
grep -rn "は作らない" --include=*.md . | grep -v ai-stock-trading
```

該当（live 文書のみ追随）:

| 箇所 | 対応 |
| --- | --- |
| `src/README.md:79`「実体が無い要素は、空フォルダを作り `.gitkeep` だけを置く（`.csproj` は作らない）」＋ §サービスユニットの標準レイアウト の構成図 | 実体化後の形へ改稿（コミット 3） |
| `docs/tech/tech-requirements.md:131`・`:146`（同旨 ＋ SharedKernel 行） | 実体化後の形へ改稿（コミット 3。trace ブロック維持・updated 前進） |

除外（理由つき）: `.ai-context/specs/*`・`.ai-context/adr/*` の同旨記述は**当時の記録**であり
追随させない（凍結。ただし IADR-0027 / IADR-0218 / IADR-0219 は live な権威記録として
日付つき追記ブロックで改定を注記した —— 本文の書き換えはしない）。
`docs/tech/composable-component-guide.md:136`「空フォルダは作らない」は
**プロジェクト内側の区分フォルダ**の条文であり 8 要素の話ではない（階層が違う）。
`scripts/README.md:294` ほかの「作らない」ヒットは無関係（検査器・UI の文脈）。

### 走査 3 — FeedbackService 移送で追随が要る参照

```
grep -rln "FeedbackService.Api.Foundation" src --include=*.cs | grep -v ai-stock-trading  # → 7 件
grep -rn "FeedbackService.Api/" docs/    # → 0 件（.cs パス参照なし）
grep -rn "ResultTests\|EncapsulationTests" docs/  # → 0 件（Kernel テストは docs/tests 記載義務なし = warn 扱い）
```

7 件 = Program.cs / AnswerFeedback.cs / FeedbackDbContext.cs / FeedbackEndpoints.cs /
Migrations 2 件 / TestWebApplicationFactory.cs。すべて移送対象または名前空間追随の対象。
`docs/tests/FR-08_answer-feedback.md` はクラス名（`FeedbackEndpointTests` 等）で参照しており、
クラス名は変えないため追随不要（`FeedbackEndpoints.cs` の裸のファイル名参照は `.cs` パス検査の
対象形式〔`src/` 始まり〕ではない）。

## 実装内容（コミット計画）

1. `docs(NFR,IADR-0280)`: IADR-0280 新設・IADR-0027 / 0218 / 0219 追記・索引・本仕様書（draft）。
2. `feat(NFR,IADR-0280)`: `Platform.Shared.Kernel` へ DDD 基底型 ＋ ユニットテスト。
3. `feat(NFR,IADR-0280)`: 4 要素 × 14 サービスの csproj 実体化・slnx 登録・`.gitkeep` 整理・
   live 文書の追随。
4. `refactor(NFR,IADR-0280)`: FeedbackService 移送（Domain → `<Svc>.Domain`、
   Persistence ＋ Migrations → `<Svc>.Infrastructure`、Endpoints / Program は Api に残置。
   snapshot / designer の CLR 型名文字列を新名前空間へ追随）。
5. `feat(NFR,IADR-0280)`: `check-unit-dependencies.js` 規則 3 ＋ 自己試験。
6. `docs(NFR)`: 本仕様書を実測で確定（status: done）。

## 受け入れ基準（実測で確定済み）

- [x] `dotnet build src/platform/backend/backend.slnx` / `src/knowledge/backend/backend.slnx` が緑
      （ビルド時間は下の実測）。
- [x] `dotnet test`: Platform.Shared.Kernel.Tests **42 件**（基底型の追加分 16 件を含む）/
      FeedbackService.Api.Tests **21 件**、いずれも Failed 0。
- [x] `dotnet format <slnx> --verify-no-changes` が両ユニットで緑。
- [x] `node scripts/check-unit-dependencies.js` 緑（csproj 196 件 / .cs 1821 件を走査。
      規則 3 の自己試験 16 件を含む 29 件 OK。実ツリーでの変異 2 種
      〔Domain → Application の ProjectReference・Domain への
      `using Microsoft.EntityFrameworkCore;` 混入〕が赤になることを実測し、復元済み）。
- [x] `node scripts/check-backend-libraries.js` 緑（規則 2 が `*.Domain.csproj` 14 件を実対象化。
      既知残件 11 件は baseline 済みのまま）。
- [x] `node scripts/check-adr-numbering.js` 緑（IADR-0280。重複・欠番なし・索引と双方向一致）。
- [x] `node scripts/check-commit-messages.js --range d451ada..HEAD` 緑（検査対象 5 件 / 除外 0 件）。
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 緑（**615 件**）。
- [x] FeedbackService の移送が振る舞いを変えていない —— 既存テスト 21 件はアサーション無修正
      （変更は `using` の追随 1 行のみ）で緑。`dotnet ef migrations has-pending-model-changes` が
      「No changes have been made to the model since the last migration.」（差分ゼロ）。
- [x] 残り 13 サービスの移送は後続波の射程（本仕様書 §射程外・IADR-0280 決定 1）。

## 実測の記録

### ビルド時間（`dotnet build <slnx> -t:Rebuild`。同一マシン・restore 済み）

| slnx | 前（波 0 HEAD） | 後（本作業後。2 回測定） |
| --- | --- | --- |
| platform | 8.3 秒 | 7.1 秒 / 8.9 秒 |
| knowledge | 22.8 秒 | 14.5 秒 / 15.8 秒 |

**56 プロジェクト（中身は csproj のみ）の追加による有意な増加は観測されなかった**
（測定間のばらつきが差分を上回る。「前」の knowledge 22.8 秒は直前の初回 restore の
キャッシュ暖機を含んでいた可能性がある。空プロジェクトのコンパイルは軽い）。

### 検査コマンドの結果一覧（最終時点）

| コマンド | 結果 |
| --- | --- |
| `dotnet build`（両 slnx） | 緑（0 Error） |
| `dotnet test` Kernel.Tests / FeedbackService.Api.Tests | 42 / 21 件 すべて Passed |
| `dotnet format --verify-no-changes`（両 slnx） | 緑 |
| `node scripts/check-unit-dependencies.js`（--self-test 含む） | 緑（自己試験 29 件） |
| `node scripts/check-backend-libraries.js` | 緑 |
| `node scripts/check-adr-numbering.js` | 緑 |
| `node scripts/check-commit-messages.js --range d451ada..HEAD` | 緑（5 件） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 緑（615 件） |
| `node scripts/check-test-spec-coverage.js` | 緑（新規テストクラス 3 件は warn = 記載義務なしの区分。baseline 変更なし） |
| `node scripts/check-trace-blocks.js` / `check-doc-links.js` / `check-doc-updated.js` / `check-cross-repo-refs.js` / `check-plan-id-qualification.js` / `check-reading-budget.js` | 緑 |
| `dotnet ef migrations has-pending-model-changes`（FeedbackService） | 差分ゼロ |

### 実行できなかった検証と理由

- **実 DB / 実ブローカでの統合試験**: 本環境に Docker が無く Testcontainers 系は実行不可。
  本作業はコードの物理配置の移送であり、単体・エンドポイントテスト（InMemory）と
  EF モデル差分ゼロの実測で「振る舞いを変えない」を担保した。
- **CI と同一の全ジョブ実行**: ローカルでは上表の同一コマンドを個別に実行した。

### 作業中の実測による決定の訂正

- IADR-0280 決定 4 の初稿は `Microsoft.EntityFrameworkCore.Design` を Infrastructure 側へ移すと
  書いていたが、**EF Core Tools は startup project（Api）側の Design 参照を要求する**
  （`PrivateAssets="all"` は推移しないため。エラーメッセージで実測）。決定 4 を
  「Design は Api / Worker に残す」へ訂正済み（本 PR 内の起草中訂正であり、凍結後の追記ではない）。
