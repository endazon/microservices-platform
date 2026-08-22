---
title: 作業仕様書 — 段 1: 認可スコープ契約へ名前つき分岐（AccessScopeBranch）を追加する
type: spec
status: draft
related_ids: [FR-05, FR-19, UC-11, ADR-0004, ADR-0036, ADR-0046]
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/07_adr/ADR-0046_private-note-not-synced-to-wikijs.md
---

# 作業仕様書: 段 1 —— 契約へ名前つき分岐を追加する

**IADR-0253 決定 6 の段 1。** 同 IADR 決定 1・2 を実装する。

## 走査基準

| 対象 | ref |
| --- | --- |
| 実装 | `origin/develop` = `346e1b87` |
| 計画 | `origin/main` = `0152962` |

## 1. これは「実装に閉じた判断」か

**委任済みである。**計画 3 文書（`07_abac-attribute-model` §選言 / `ADR-0046` D-06 / `ADR-0054` フォローアップ 3）が
**契約の改定方針を実装 IADR へ明示的に委任**しており、方針は `IADR-0253` で確定済みである。
**本段は確定した方針の実装であり、新たに決めることは無い。**

**計画側の判定規則（`read` の 3 節の選言）は変えない。**

## 2. 段 1 の射程

**契約に型を足すだけ。** 評価器も消費側も変えない。

| 変える | 変えない |
| --- | --- |
| `AccessScopeBranch` の新設 | `AbacEvaluator`（段 2） |
| `AccessScopeResponse` へ `Branches` を追加（既定 `null`） | 消費側 4 サービス（段 3） |
| 契約スキーマ baseline | `AllowedFilters` の算出（**据え置き**。`IADR-0253` 決定 2） |

**段 1 の時点で `Branches` を書き込む生産者は 1 つも無い。常に `null` である。**

## 3. 🔴 「緑」の意味が段によって違う —— 段 1 だけが「緑＝成功」と読める

| 段 | tripwire（`AbacUnenforcedAxisTests`） | 「緑」の意味 |
| --- | --- | --- |
| **1（本段）** | **緑のままが正しい** | ✅ **緑＝成功。** 消費側を変えておらず `Branches` は常に `null` のため、挙動は 1 ビットも変わらない |
| 2 | 緑のまま（消費側が未対応） | ⚠️ 緑は「退行が無い」までしか意味しない。**評価器の単体テストで分岐 2 本を直接 assert する** |
| **3** | 🔴 **赤くなるのが正しい** | 🔴 **緑＝失敗の可能性。** 書き換えずに緑なら**分岐が効いていない** |
| 4・5 | 段 3 の書き換え後の形で緑 | 陽性対照つきで判定 |

🔴 **本段の PR に、この表を予告として載せる。**段 2 以降で緑が出たときに段 1 と同じに読まれないようにする。
**段ごとに緑の意味が変わることを、最初の段で宣言しておく。**

## 4. 実装

`src/platform/backend/Shared/Platform.Shared.Contracts/Dtos/AccessScopeDto.cs`。

```csharp
public record AccessScopeBranch(string Name, List<AttributeFilter> Filters);

public record AccessScopeResponse(
    string UserId,
    List<AttributeFilter> AllowedFilters,
    bool Granted = false,
    List<AccessScopeBranch>? Branches = null);   // ← 末尾へ追加
```

### 🔴 位置と既定値の制約（契約検査の実測から）

`scripts/check-contract-schema.js` の判定（`IADR-0122` 決定 2）:

| 変更 | 判定 |
| --- | --- |
| **既定値付きの新メンバーの追加** | **非破壊** ✅ 本段はこれに当たる |
| 既定値の無いメンバーの追加 | **破壊的**（旧発行者のメッセージが必須項目を欠く） |
| **位置変更** | **破壊的** |

**したがって `Branches` は必ず末尾へ置き、`= null` を付ける。** 途中へ挿すと既存の位置引数呼び出しが壊れる。

**非破壊でも baseline に差分がある限り `EXIT=1` になる**（スナップショットテスト）。`--update` で baseline を
更新し、**その差分を PR のレビュー対象にする**のが同検査の主眼であるため、差分を PR 本文へ載せる。

## 5. 評価規則（契約コメントとして書く）

- `Granted == false` → 不可視
- `Granted == true` かつ `Branches` が空／`null` → **従来どおり `AllowedFilters` で評価**
- `Branches` が 1 件以上 → **いずれかの分岐のフィルタをすべて満たす文書が可視**（分岐内 AND・分岐間 OR）

**移行中の安全性は包含関係から従う** —— `AllowedFilters`（分岐の積に相当）は `Branches`（分岐の和）の
**部分集合**であるため、**未移行のサービスが余分に見せることは構造上あり得ない。**

## 6. 受け入れ基準

- [ ] `AccessScopeBranch` が契約に存在する
- [ ] `AccessScopeResponse.Branches` が**末尾**・既定 `null`
- [ ] **既存テストが全緑のまま**（挙動が変わっていないこと）
- [ ] `check-contract-schema.js` が**非破壊**と判定する（`$acceptedBreakingChanges` へ追記されない）
- [ ] baseline の差分が PR に載っている
- [ ] 新設した型・メンバーの単体テストがある（既定が `null` であること・分岐の意味論）

### 変異試験

| 変異 | 落ちるべき検査 |
| --- | --- |
| `Branches` から `= null` を外す | `check-contract-schema.js` が**破壊的**と判定する |
| `Branches` を `Granted` の前へ挿す | 同上（**位置変更**） |

**両方を実測してから完了とする。** 変異が当たったことを先に確認する（`git diff` が当該箇所のみ・ビルド `EXIT=0`）。

## 7. 母集合（規則 6）

**`AccessScopeResponse` を参照する面を `origin/develop` 全域で走査した。**

**走査 ref は `origin/develop` = `346e1b87`。走査範囲は `src/` 配下**（コード）。

| 面 | 件数 | 本段での扱い |
| --- | --- | --- |
| `AccessScopeResponse` を参照するファイル（`src/`） | **35** | **変えない**（既定値付き追加のため無改修で通る） |
| `AttributeFilter` を参照するファイル（`src/`） | **37** | 変えない |
| 陽性対照 `AccessScope`（総称・`src/`） | **45** | 走査形が効いていることの確認 |
| 契約定義 `AccessScopeDto.cs` | 1 | **変更対象** |
| `scripts/contract-schema-baseline.json` | 1 | **`--update` で更新** |

### 🔴 ［着手後に追加］母集合を 2 つ引き漏らしていた

**上の表は「C# の参照」だけを引いており、契約が持つ他の面を落としていた。**
着手後に検査器が 2 つとも検出した。**記録として残す。**

| 落としていた面 | 検出した検査器 | 対応 |
| --- | --- | --- |
| `docs/api/openapi.yaml` の `AccessScopeResponse` スキーマ | `check-openapi-dto-drift.js`（`branches` が C# に在るが契約に無い） | **スキーマへ `branches` を追加** |
| `src/platform/frontend/.../generated/bff.schemas.ts`（orval 生成物） | CI の再生成差分検査（**ローカルでは走らせていなかった**） | **`pnpm run codegen` で再生成** |

**引き漏らした理由**: `AccessScopeResponse` を **C# の識別子としてだけ**走査した
（`git grep -l 'AccessScopeResponse' -- 'src/*'`）。**契約は C# だけに在るのではない** ——
OpenAPI スキーマと、そこから生成されるフロントの型にも同じ契約が写っている。

**規則 5（軸を 1 本で終わらせない）の適用漏れである。** 1 軸目は C# の参照、
2 軸目は**契約の表現形**（C# / OpenAPI / 生成物）で引くべきだった。

**`branches` は BFF 面（`/bff/` 配下のパスが参照するスキーマ）に載るため、生成物にも出る。**
`AccessScopeResponse` が生成物に在ることは `grep -rl 'AccessScopeResponse' src/*/frontend/src` で確認した。

**是正後の母集合（全 6 ファイル）**:

| # | ファイル | 変更 |
| --- | --- | --- |
| 1 | `Platform.Shared.Contracts/Dtos/AccessScopeDto.cs` | 型とメンバーの追加 |
| 2 | `AuthorizationService.Api.Tests/AccessScopeContractTests.cs` | テスト 4 件 |
| 3 | `scripts/contract-schema-baseline.json` | `--update` |
| 4 | `docs/api/openapi.yaml` | スキーマへ `branches` |
| 5 | `src/platform/frontend/.../generated/bff.schemas.ts` | `pnpm run codegen` の生成物 |
| 6 | 本作業仕様書 | — |

**`AccessScopeBranch` の事前走査**（新設前に既存の衝突が無いことの確認）:

| 走査範囲 | 件数 |
| --- | --- |
| `src/` 配下（**コード**） | **0** ＝ 未存在。新設してよい |
| リポジトリ全体 | **1** —— `.ai-context/adr/IADR-0253_*.md`（**本段の方針を決めた IADR 自身の C# スケッチ**。コードではない） |

🔴 **範囲を分けて書く。**「リポジトリ全体で 1 件」だけを見ると既存実装があるように読め、
「`src/` で 0 件」だけを見ると IADR の記述を見落とす。**同じ ref でもコードと文書で答えが変わる。**

**除外**: `src/ai-stock-trading`（submodule。別プロジェクトの名前空間であり本契約を参照しない）。

## 8. 射程外

- 評価器が分岐を組み立てること（段 2）
- 消費側の分岐対応（段 3）／`DocumentShare`（段 4）／`Action`（段 5）
- 🔴 **`IADR-0253` 決定 5 の改定**（`PolicyAction` に `write` が無い件）。**段 5 の着手前に行う**
