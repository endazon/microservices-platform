---
title: 作業仕様書 — knowledge ユニットを FluentAssertions から AwesomeAssertions へ移す（#455 A-3 段 2）
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

# 作業仕様書: knowledge ユニットを AwesomeAssertions へ移す（#455 A-3 段 2）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: `ADR-0030`（バックエンドアプリケーション層標準）§不採用ライブラリ
- 実装 issue: `#455`

## 前段（段 1）で確立したこと

段 1（platform・PR #880）で**手順と危険箇所が確定した**。本段はそれをそのまま適用する。

| 段 1 で分かったこと | 本段への影響 |
| --- | --- |
| 置換は **`using` 行のみ**（修飾参照 0 件） | 本段でも実測 **0 件**。機械的に置換できる |
| baseline は **1 プロジェクト = 1 エントリ**。消し忘れると CI が fail | 11 エントリを**全部**消す |
| フォークだが `BeEquivalentTo` などは**実テストで確認する** | 同じ表明を knowledge 側でも集計して確認する |
| ratchet は**両方向**に効く（新規混入 / 減らし忘れ） | 変異試験で再確認する |

## 着手前の実測

| 項目 | 実測 |
| --- | --- |
| `using FluentAssertions` を含む `.cs`（knowledge） | **96** |
| `PackageReference` を持つ `.csproj`（knowledge） | **11** |
| `FluentAssertions.` の**修飾参照** | **0 件** |
| baseline の `FluentAssertions` エントリ | **11**（段 1 で 14 → 11） |

### 対象プロジェクト（11）

| プロジェクト | ファイル |
| --- | ---: |
| `DataSourceService.Api.Tests` | 18 |
| `Knowledge.IntegrationTests` | 16 |
| `ConversionService.Worker.Tests` | 13 |
| `DocumentService.Api.Tests` | 11 |
| `AiAnalysisService.Api.Tests` | 11 |
| `WikiService.Api.Tests` | 8 |
| `RetrievalService.Api.Tests` | 6 |
| `IngestionService.Worker.Tests` | 6 |
| `FeedbackService.Api.Tests` | 3 |
| `DashboardService.Api.Tests` | 3 |
| `Knowledge.Contracts.Tests` | 1 |
| **計** | **96** |

## スコープ

1. 11 プロジェクトの `.csproj` の `PackageReference` を差し替える
2. 96 ファイルの `using` 行を差し替える
3. baseline から `FluentAssertions` の **11 エントリすべて**を削除する
4. **`FluentAssertions` の baseline が空になる** —— `src/Directory.Packages.props` の
   `PackageVersion` 行を削除できる状態になるので、**同時に削除する**
   （baseline の `$comment` が「baseline が空になったら削除する」と手順を定めている）

### スコープ外

- **`MassTransit` の baseline エントリ** —— Wolverine 移行（別作業）の射程である。**触らない**
- **xUnit v3 への切替（A-2）** —— 別作業

## 受け入れ基準

1. knowledge 11 プロジェクトが `AwesomeAssertions` を参照し、`FluentAssertions` を参照しない
2. `git grep -l "using FluentAssertions" -- '*.cs' ':!src/ai-stock-trading'` が **0 件**
3. baseline に `FluentAssertions` のエントリが **1 つも残っていない**
4. `src/Directory.Packages.props` から `FluentAssertions` の `PackageVersion` が消えている
5. `node scripts/check-backend-libraries.js` が **EXIT=0**
6. `dotnet build|test src/knowledge/backend/backend.slnx` が **Failed 0**、件数が減っていない
7. **統合テスト 43/43 も通る**（dockerd を起こして実測する）
8. `dotnet format --verify-no-changes` が EXIT=0
9. **変異試験**（EXIT はリダイレクトして読む。`| tail` の終了コードを読まない）
   - (a) 移行済みファイルへ `using FluentAssertions` を戻す → **EXIT=1**
   - (b) `PackageVersion` を消したのに `PackageReference` が残る状態 → ビルドが落ちる

## 挙動差の確認（段 1 と同じ作法）

**「フォークだから同じはず」で済ませない。** knowledge 側で使われている表明を集計し、
**挙動が変わりやすいものを名指しで**確認する。**すべて実テストで使われているため、
挙動が変われば落ちる。**

### 🔴 実際に API 差が出た —— `BeGreaterOrEqualTo` は存在しない

**段 1 で「フォークなので API 名は変わらない見込み」と書いたが、本段でその見込みが破れた。**

```
error CS1061: 'NumericAssertions<int>' does not contain a definition for 'BeGreaterOrEqualTo'
  src/knowledge/backend/Tests/Knowledge.IntegrationTests/DocumentService/DocumentCrudTests.cs:83
```

- `BeGreaterOrEqualTo` は **FluentAssertions v7 の非推奨エイリアス**である。フォークは
  **新名 `BeGreaterThanOrEqualTo` だけを残し、エイリアスを落としている**。
- **段 1 が緑だったのは、この 1 箇所が knowledge ユニットにあったからにすぎない。**
  「段 1 で API 差は無かった」から「フォークに API 差は無い」を導いてはならない。
- **私自身の事前集計に `BeGreaterOrEqualTo` は 1 件として載っていた。**
  載っていたのに**非推奨エイリアスだと気付かず**危険リストへ挙げなかった。
  → **集計は「件数の多い表明」ではなく「非推奨エイリアス」で引くべきだった。**

**是正後に非推奨エイリアスを名指しで走査し直した**（`BeGreaterOrEqualTo` / `BeLessOrEqualTo` /
`ShouldBeEquivalentTo`）。**残りは 0 件**で、該当はこの 1 箇所だけだった。

| 表明（knowledge。出現回数） | 件数 | 確認 |
| --- | ---: | --- |
| `Be` | 517 | 全数通過 |
| `Contain` / `NotContain` | 68 / 19 | 全数通過 |
| `BeEquivalentTo`（リフレクション再帰比較） | 14 | 全数通過 |
| `MatchRegex` / `NotMatchRegex` | 25 / 3 | 全数通過 |
| `Throw` / `ThrowAsync` | 5 / 5 | 全数通過 |
| **`BeGreaterOrEqualTo`（非推奨エイリアス）** | **1** | 🔴 **コンパイルエラー。新名へ是正** |

**確認の根拠はビルドとテストの全数通過である** —— 表明が実際に走らないと「同じ」とは言えない。
統合テスト 43/43 も dockerd を起こして実走した。

## 母集合（規則 9・10）

**誤りの側（`FluentAssertions`）の文字列で、拡張子で絞らずパス除外だけで引く。**

```
git grep -n "FluentAssertions" -- . ':!src/ai-stock-trading'
```

🔴 **規則 10 の予告**: 本段で **baseline が空になる**ので、
「**残件がある**」「**FluentAssertions を広範に使用中**」と書いた自分の記述が偽になる。
`docs/tech/tech-requirements.md`（段 1 で直したばかりの箇所を**また**直すことになる）と
`src/Directory.Packages.props` のコメントを**移した後に引き直す**。

### 結果（実装後に確定した値）

| 分類 | 件数 | 扱い |
| --- | ---: | --- |
| **置換した** `.cs`（knowledge） | **96** | `using` 行のみ。修飾参照は 0 件だった |
| **置換した** `.csproj` | **11** | `PackageReference` の 1 行ずつ |
| **baseline から削除**したエントリ | **11** | うち 4 件は `FluentAssertions` 単独だったのでエントリごと削除 |
| **API 差で書き換えた**呼び出し | **1** | `BeGreaterOrEqualTo` → `BeGreaterThanOrEqualTo` |
| **削除した** `PackageVersion` | **1** | `FluentAssertions 7.2.0`（baseline が空になったので手順どおり削除） |
| **追随させた記述** | **5 箇所** | 下表 |

**移行後の実測（すべて再計算した。走査結果の読み取りではない）:**

```
using FluentAssertions  を持つ .cs（AST 除外） : 0    （移行前 96）
PackageReference FluentAssertions の .csproj   : 0    （移行前 11）
using AwesomeAssertions を持つ .cs             : 154  （150 置換 + 共有カーネル 2 + 雛形 2）
PackageReference AwesomeAssertions の .csproj  : 16
baseline 残件                                  : 15 件 / 15 プロジェクト（すべて MassTransit）
```

**除外したもの（理由つき）:**

- **検査器の実装と自己試験**（`check-backend-libraries.js` / `scripts.repo.test.js`）——
  `FluentAssertions` は**禁止対象の名前として**現れる。移行しても消えない。誤りではない。
  ただし同ファイルの**散文コメント**「現行実装は MassTransit / FluentAssertions を広範に使用中」は
  **偽になったので直した**（下表 #3）。**「禁止語の列挙」と「現状の説明」を分けて判定する。**
- **`.github/workflows/ci.yml` / `templates/unit-template/` / `docs/operations/operations.md`
  / `scripts/README.md`** —— いずれも「不採用ライブラリの一覧」。**不採用である事実は変わらない。**
- **`MassTransit` の baseline 15 件** —— Wolverine 移行の射程。**1 件も触っていない。**
- **`.ai-context/specs/` / `.ai-context/superpowers/`（凍結記録）** —— 本文を書き換えない。
  `superpowers/plans/2026-06-26-P0-foundation.md` は当時のコード片をそのまま含むが、
  **その時点の記録として正しい**。
- **`.ai-context/adr/`** —— 本文は書き換えないが、**日付つき追記は可**（`traceability.repo.md`）。
  現状を偽って読ませる 2 件だけ追記した（下表 #4・#5）。

### 規則 10 —— この変更で新たに誤りになる自分の記述

**「残件」「広範」「使用中」「ratchet 管理下」で引き直した**（**是正前の語 `FluentAssertions` だけで
引いても捕まらない**種類の記述がある。これが規則 10 の眼目である）。

| # | 場所 | 従前 | 是正後 |
| --- | --- | --- | --- |
| 1 | `docs/tech/tech-requirements.md`（ratchet 説明） | 「MassTransit / FluentAssertions を広範に使用中（`.csproj` 15 / 11、`.cs` 36 / 96）」 | 「MassTransit を広範に使用中（`.csproj` 15、`.cs` 36）」 |
| 2 | 同（残件） | 「42 → 29 → 26 件（MassTransit / FluentAssertions）」 | 「42 → 29 → 26 → **15** 件（MassTransit **のみ**）」＋ 消化の記録と追記ブロック |
| 3 | `scripts/check-backend-libraries.js` 冒頭コメント | 「MassTransit / FluentAssertions を広範に使用中のため」 | 「MassTransit を広範に使用中のため（FluentAssertions は消化済み・残件 0）」 |
| 4 | `.ai-context/adr/IADR-0216`（結果・フォローアップ） | 「残る残件は MassTransit / FluentAssertions の 29 件」「削除対象は 2 群」 | 日付つき追記で「15 件・1 群になった」と現状を併記 |
| 5 | `.ai-context/adr/IADR-0229` ＋ `Platform.Shared.Kernel.Tests.csproj` | 「FluentAssertions は…**ratchet 管理下**」 | **ratchet 管理下ではなくなった**（baseline・CPM から消えた。`BANNED` には残る）と追記 |

🔴 **同じ節を同じ日に二度直した。** #1・#2 は**段 1 で直したばかりの箇所**である。
段 1 の時点では「11 プロジェクト残っている」が正しく、段 2 で 0 になった。
**段階移行では、中間状態を書いた記述が次の段で必ず偽になる** —— 分割の代償として引き受ける。

### 変異試験（EXIT はリダイレクトして読む）

`cmd | tail; echo $?` は **`tail` の終了コード**を読む。パイプせず、出力をファイルへ落として読む。

| 変異 | 期待 | 実測 |
| --- | --- | --- |
| 基準（無変異） | EXIT=0 | **EXIT=0** |
| (a) 移行済み `.cs` へ `using FluentAssertions` を戻す | 検査器が落ちる | **EXIT=1** |
| (b) `PackageVersion` を消したまま `PackageReference` を残す | ビルドが落ちる | **EXIT=1**（`NU1010`） |

**復旧を確認した**（`using AwesomeAssertions;` / `Include="AwesomeAssertions"` / 復旧後 EXIT=0）。
