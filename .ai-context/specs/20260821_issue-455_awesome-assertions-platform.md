---
title: 作業仕様書 — platform ユニットを FluentAssertions から AwesomeAssertions へ移す（#455 A-3 段 1）
type: spec
status: done
related_ids:
  - ADR-0030
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - "ADR-0030（バックエンドアプリケーション層標準・不採用ライブラリ）"
related_adrs:
  - IADR-0116
issue: "#455"
---

# 作業仕様書: platform ユニットを AwesomeAssertions へ移す（#455 A-3 段 1）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: `ADR-0030`（バックエンドアプリケーション層標準）§不採用ライブラリ
- 実装 issue: `#455`（アプリケーション層標準への全面移行）

## なぜ移すのか

**FluentAssertions は v8 で商用ライセンスへ移行した。** `ADR-0030` の不採用ライブラリに載っており、
`scripts/backend-library-baseline.json` が **ratchet** で管理している。

> ratchet: 新規混入は fail、ここに載る残件は warn、消えたのに残っていれば fail。
> 各サービスの再実装 issue（#438〜#451）は移行と同時に自プロジェクトの行を削除すること。
> baseline が空になったら Directory.Packages.props から不採用パッケージを削除する。

**AwesomeAssertions は FluentAssertions v7 系のフォーク**（NuGet の説明が `"A fork of FluentAssertions."`）で、
**CPM に `9.5.0` が宣言済み**である。

## 着手前の実測

| 項目 | 実測 |
| --- | --- |
| `using FluentAssertions` を含む `.cs`（AST 除外） | **150 ファイル** |
| うち **platform ユニット**（本段の射程） | **54 ファイル / 3 プロジェクト** |
| `FluentAssertions.` の**修飾参照** | **0 件**（`using` 行だけを替えればよい） |
| baseline の `FluentAssertions` エントリ | **14**（knowledge 11 ＋ platform 3） |
| 既に移行済みのプロジェクト | **2**（`Platform.Shared.Kernel.Tests` / 雛形の `SampleService.Tests`） |
| AST submodule | **移行済み・0 件**（自前の CPM を持ち本リポと非共有） |

### 本段の射程（platform ユニットのみ）

| プロジェクト | ファイル |
| --- | ---: |
| `Platform.Bff.Tests` | 24 |
| `LlmGateway.Api.Tests` | 18 |
| `AuthorizationService.Api.Tests` | 12 |
| **計** | **54** |

**knowledge ユニット（96 ファイル / 11 プロジェクト）は次段**とする。
1 PR に 150 ファイルを入れると [[IADR-0116]] 規約 4「レビュー可能な変更単位」を超える。

## スコープ

1. 3 プロジェクトの `.csproj` の `PackageReference` を `FluentAssertions` → `AwesomeAssertions` へ
2. 54 ファイルの `using FluentAssertions;` → `using AwesomeAssertions;`
3. `scripts/backend-library-baseline.json` から **platform の 3 エントリを削除**
   （削除し忘れると「消えたのに baseline に残っている」で CI が fail する）

### スコープ外

- **knowledge ユニット 11 プロジェクト**（次段）
- **CPM から `FluentAssertions` の `PackageVersion` 行を消すこと** —— baseline が空になってからである
  （knowledge がまだ使っている）
- **xUnit v3 への切替（A-2）** —— 別作業。**`Xunit.SkippableFact` の扱いは A-2 の射程に含める**
  （v2 には動的スキップの手段が無く、v3 の `Assert.Skip` を待つほうが退化しない）

## 受け入れ基準

1. platform の 3 プロジェクトが `AwesomeAssertions` を参照し、`FluentAssertions` を参照しない
2. `git grep -l "using FluentAssertions" -- 'src/platform/**/*.cs'` が **0 件**
3. `scripts/backend-library-baseline.json` の platform 3 エントリが消えている
4. `node scripts/check-backend-libraries.js` が **EXIT=0**（新規混入なし・減らし忘れなし）
5. `dotnet build src/platform/backend/backend.slnx` が 0 Error
6. `dotnet test src/platform/backend/backend.slnx` が **Failed 0**、かつ**件数が減っていない**
   （Kernel 26 / LlmGateway 183 / Bff 231 / Authorization 68）
7. `dotnet format --verify-no-changes` が EXIT=0
8. **変異試験**: (a) 置換後のファイルへ `using FluentAssertions` を 1 つ戻すと検査器が **fail**
   (b) baseline のエントリを消し忘れた状態にすると検査器が **fail**

## 挙動差のリスク（フォークとはいえ確かめる）

**フォークなので API 名は変わらない見込み**だが、**「見込み」で済ませない。**
本リポジトリで実際に使われている表明を集計し、**挙動が変わりやすいものを名指しで確認する。**

| 表明 | 件数 | なぜ危ないか |
| --- | ---: | --- |
| `BeEquivalentTo` | 21 | **リフレクションで再帰比較する。** オプションの既定値がフォーク間でずれると、通っていた比較が通らなくなる（または逆に緩くなる） |
| `BeApproximately` | 1 | 浮動小数の許容誤差の扱い |
| `BeAfter` | 1 | 日時比較の境界 |
| `MatchRegex` / `NotMatchRegex` | 25 / 3 | 正規表現エンジンのオプション |

**テストが全数通ることをもって確認とする** —— 上記はいずれも実テストで使われているため、
挙動が変われば落ちる。**落ちなければ「同じ」と言えるのは、その表明が実際に走っている場合だけ**である。

## 母集合（規則 9・10）

**着手前に引く。誤りの側（`FluentAssertions`）の文字列で、拡張子で絞らずパス除外だけで取る。**

```
git grep -n "FluentAssertions" -- . ':!src/ai-stock-trading'
```

### 結果（実装後に確定した値）

| 分類 | 件数 | 扱い |
| --- | ---: | --- |
| **置換した** `.cs`（platform） | **54** | `using` 行のみ。修飾参照は 0 件だった |
| **置換した** `.csproj` | **3** | `PackageReference` の 1 行ずつ |
| **baseline から削除**したエントリ | **3** | いずれも `FluentAssertions` 単独だったのでエントリごと削除 |
| **追随させた導出値**（`docs/tech/tech-requirements.md`） | **3 箇所** | 下記 |
| **残る** `.cs`（knowledge。次段） | **96** | 本段の射程外 |

**除外したもの（理由つき）:**

- **検査器の実装と自己試験**（`check-backend-libraries.js` / `scripts.repo.test.js`）——
  `FluentAssertions` は**禁止対象の名前として**現れる。移行しても消えない。誤りではない。
- **`src/Directory.Packages.props` の `PackageVersion`** —— knowledge がまだ使っているので**残す**。
  同ファイルのコメントが「baseline が空になったら削除する」と手順を書いており、それに従う。
- **`.github/workflows/ci.yml` / `templates/unit-template/README.md` / `docs/operations/operations.md`**
  —— いずれも「不採用ライブラリの一覧」として名前を挙げている。**不採用である事実は変わらない。**
- **`Platform.Shared.Kernel.Tests.csproj` / `SampleService.Tests.csproj` のコメント** ——
  「FluentAssertions は商用化のため不採用」と**理由**を書いている。誤りではない。
- **凍結記録**（`.ai-context/`）—— 本文を書き換えない運用。

### 規則 10 —— この変更で新たに誤りになる自分の記述

**移した後の語で引き直した。** `docs/tech/tech-requirements.md` の**導出値 3 箇所**が該当した。

| # | 記述 | 従前 | 実測 | 私の変更が原因か |
| --- | --- | ---: | ---: | :-: |
| 1 | `FluentAssertions` の `.csproj` 数 | 14 | **11** | ✅ **原因** |
| 2 | baseline 残件 | 29 | **26** | ✅ **原因** |
| 3 | v3 移行の対象テストプロジェクト数 | 30 | **16** | ❌ **元から誤り** |

🔴 **引き直したら、私の変更と無関係に古い数値も出てきた**（規則 7「数値を 1 つ直したら、
その値を持つファイルを全走査し直す」）。

- `MassTransit` の `.cs`: **59 と書いてあったが実測 36**
- `FluentAssertions` の `.cs`: **129 と書いてあったが、移行前の時点で 150**（移行後 96）
- テストプロジェクト数「30」は**根拠が無い**。`Microsoft.NET.Test.Sdk` を参照する `.csproj` は **16** である

**すべて実測値へ直した。** あわせて 🔴 **`src/ai-stock-trading` は自前の `Directory.Packages.props` を持ち
本リポと CPM を共有しない**ことを明記した —— これは A-2（v3 一斉切替）の射程を決める最重要の事実であり、
「30 プロジェクトが同時に移らざるを得ない」という記述はこの点でも誤解を招く。
