---
title: 作業仕様書 — Worker 2 つを統合テストへ載せる（#455 Phase 0 / U0b）
type: spec
status: done
related_ids:
  - ADR-0027
  - UC-04
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - "ADR-0027（メッセージング基盤 = Wolverine。移行チェックリスト手順 3）"
related_adrs:
  - IADR-0219
issue: "#455"
---

# 作業仕様書: Worker 2 つを統合テストへ載せる（#455 Phase 0 / U0b）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: `ADR-0027` 移行チェックリスト **手順 3**（リスニングキュー名にサービス名を前置する）
- 実装 issue: `#455` / `#441`

## なぜ要るのか

U0a（PR #884）で統合テストは**本番の配線を通る**ようになった。しかし
**`DocumentUpdated` の 2 購読者が同時に生きている状態はまだ作れない**。

```
DocumentUpdated: 発行 [knowledge/DocumentService]
               → 購読 [knowledge/IngestionService, knowledge/WikiService]
```

**購読者の一方（IngestionService）は Worker であり、統合テストが参照していない。**
移行手順 3 を誤って competing consumer 化すると**片方だけがメッセージを受け取る**が、
**2 つを同時に立てるテストが無い限り試験できない**。

## 着手前の実測

| 項目 | 実測 |
| --- | --- |
| 統合テストの基準 | **43 / 43 通過**（U0a 着地後） |
| `Knowledge.IntegrationTests.csproj` の `ProjectReference` | **7**（Worker は **0 件**） |
| `IngestionService.Worker` の `DbContext` | 🔴 **無し**（`AddDbContext` 0 件） |
| `ConversionService.Worker` の `DbContext` | **`ConversionJobDbContext`（あり）** |
| 両 Worker の `Program` 公開 | **`public partial class Program { }` を既に持つ** |

### 🔴 障害は 2 つだけ（新しい仕組みは要らない）

1. **`ProjectReference` が無い**
2. **両 Worker が公開するのはグローバル名前空間の `Program`** なので、2 つ参照すると
   **型が衝突する** —— Api 系 5 サービスが `TestMarker.cs` を持つのはこのためである

つまり U0b は **既存パターンの踏襲**である。

### 🔴 ただし基底が DbContext を要求する

```csharp
public abstract class IntegrationTestFactoryBase<TProgram, TDbContext>
    where TDbContext : DbContext        // ← IngestionService.Worker は DbContext を持たない
```

## スコープ

1. **`IntegrationTestFactoryBase<TProgram>`（DbContext を要求しない）を切り出す**
   - 共通処理（config 上書き・`MassTransitHostOptions`・認証・`AdditionalServices`）を持つ
   - `protected virtual void ReplaceDbContext(IServiceCollection services) { }`（既定は何もしない）
2. **既存の `<TProgram, TDbContext>` をその派生にする** —— `ReplaceDbContext` だけを override する
   （**既存 5 ファクトリの宣言は 1 文字も変えない**）
3. `IngestionService.Worker` / `ConversionService.Worker` に **`TestMarker.cs`** を追加
4. `Knowledge.IntegrationTests.csproj` へ **`ProjectReference` 2 件**
5. **`IngestionServiceFactory`（1 引数版）/ `ConversionServiceFactory`（2 引数版）** を追加

### スコープ外（別 PR）

- **U0c**（`DocumentUpdated` の 2 購読者同時受信テスト）—— 本 PR は**器を用意するところまで**。
  テストの新設は独立したレビュー単位にする
- **U0d**（`Pipeline:ConfigPath` を実 `pipeline.json` へ向ける）

## 受け入れ基準

1. `IntegrationTestFactoryBase<TProgram>` が存在し、`<TProgram, TDbContext>` がその派生である
2. **既存 5 ファクトリの宣言が変わっていない**（基底の切り出しが既存に波及していない）
3. Worker 2 つが `ProjectReference` され、`TestMarker` 経由でホストできる
4. **既存 43 テストが緑のまま**（1 件も減らない・落ちない）
5. `dotnet build|test` 両ユニットが **Failed 0**、件数が減っていない
6. 検査器一式・`scripts.test.js` が EXIT=0
7. `dotnet format --verify-no-changes` が両ユニットで EXIT=0

🔴 **4 が破れたら「Worker を載せたことで既存が壊れた」という発見である。テストを緩めない。**

## 変異試験

| 変異 | 期待 |
| --- | --- |
| (a) `IngestionServiceFactory` を 2 引数版（DbContext 要求）へ変える | **コンパイルエラー**（DbContext が無いことの裏返し） |
| (b) `TestMarker` を使わず `Program` を直接指す | **型の曖昧参照でコンパイルエラー**（`TestMarker` が要る理由の実証） |

**復旧を確認し、復旧したことを報告に含める。**

## 実装後に確定した結果

| 項目 | 実測 |
| --- | --- |
| 統合テスト | **43 / 43 通過**（基準と同数。件数不変） |
| 新設した基底 | `IntegrationTestFactoryBase<TProgram>`（DbContext を要求しない） |
| **既存 5 ファクトリの宣言の変更** | **0 文字**（2 引数版を 1 引数版の派生にしたため波及しない） |
| 追加した `TestMarker` | **2**（`IngestionServiceTestMarker` / `ConversionServiceTestMarker`） |
| 追加した `ProjectReference` | **2** |
| 追加したファクトリ | **2**（Ingestion は 1 引数版・Conversion は 2 引数版） |

## 変異試験（EXIT はリダイレクトして読む）

| 変異 | 期待 | 実測 |
| --- | --- | --- |
| (b) `TestMarker` をやめて `global::Program` を直接指す | 型の衝突でコンパイルエラー | ✅ **EXIT=1** —— `error CS0433: The type 'Program' exists in both 'AiAnalysisService.Api...'` |

**復旧を確認した**（変異残骸 0・復旧後ビルド EXIT=0）。

### 🔴 変異 (a) は「証明したいことを証明していなかった」

当初 **(a)「`IngestionServiceFactory` を 2 引数版へ変えるとコンパイルエラーになる」** を
用意していたが、**実測するとビルドは通った（EXIT=0）**。

理由は単純である。`IntegrationTestFactoryBase<TProgram, TDbContext>` の制約は
`where TDbContext : DbContext` だけなので、**無関係な `DocumentDbContext` を渡しても
型としては成立する**。コンパイラは「そのサービスがその DbContext を使うか」を知らない。

🔴 **したがって「基底の切り出しが必要だ」は、コンパイル時に強制される制約ではない。**
必要性の根拠は別のところにある ——

```
git grep -l 'DbContext' -- 'src/knowledge/backend/Services/IngestionService/**/*.cs'
→ 0 件（名指しできる DbContext 型がそもそも存在しない）
```

**`IngestionService.Worker` には `TDbContext` に書ける型が 1 つも無い。** 2 引数版を使うには
**他サービスの DbContext を借りる**しかなく、それは「使わない DbContext を Testcontainers の
Postgres へ差し替える」という無意味な副作用を持ち込む。**意味の問題であって型の問題ではない。**

**変異が「落ちなかった」ことを、そのまま「不要だった」と読み替えない。** 落ちなかったのは
**変異の設計が主張と対応していなかった**からであり、主張自体は上の実測（DbContext 型が 0 件）
で支えられている。**設計の誤りを開示して、根拠を測り直した。**

## 母集合（規則 9・10）

**是正後に「Worker は統合テストに載っていない」で引き直した。**

| 場所 | 従前 | 是正 |
| --- | --- | --- |
| `docs/tech/tech-requirements.md`「Wolverine 移行の前提」残る穴 2 | 「載せるには基底の切り出しが要る」 | **器は用意した**と書き換え、🔴 **テストそのものはまだ書いていない**（U0c）ことを残す |

**除外したもの（理由つき）:**

- **`.ai-context/specs/` の凍結記録**（U0a・Phase 0 前作業の仕様書）—— 執筆時点の事実として正しい。
  訂正の参照点は live 側（`docs/`）に 1 つ置く（[[IADR-0141]]）
