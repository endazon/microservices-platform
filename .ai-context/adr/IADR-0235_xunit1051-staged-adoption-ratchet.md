---
title: IADR-0235 xUnit1051 の段階採用は「許可リスト＋WarningsAsErrors」で行い、剥がしたら戻れないようにする
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0030
  - IADR-0140
  - IADR-0231
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (テスト = xUnit v3)
---

# IADR-0235 xUnit1051 の段階採用は「許可リスト＋WarningsAsErrors」で行い、剥がしたら戻れないようにする

## 状況

`IADR-0231` 決定 4 が、xUnit v3 のアナライザ `xUnit1051`（「`CancellationToken` を受ける呼び出しには
`TestContext.Current.CancellationToken` を渡せ」）を `src/Directory.Build.props` で
**テストプロジェクトのみ `NoWarn`** して据え置いた。同 ADR は「採用は #882 で ratchet により段階的に行い、
完了時に `NoWarn` を削除する」と申し送っている。本 ADR はその器（第 1 段）を決める。

着手時点で移行は **1 箇所も始まっていない**（`git grep -n "TestContext" 8abff2e -- '*.cs'` が出力なし・exit 1)。

### 🔴 実測で判明した 3 つの事実

**(1) 件数は 943 件であり、1,886 件ではない。**
`IADR-0231` 決定 4・`src/Directory.Build.props` のコメント・`docs/tech/tech-requirements.md` は
いずれも **1,886 件**と書いていたが、これは **2 倍の重複計上**である。MSBuild は 1 件の警告を
ビルド中の行と末尾のサマリの 2 箇所へ出力するため、ログ行を素朴に数えると実数の 2 倍になる。
ファイル・行・列で一意化した実数は **943 件**（16 プロジェクト中 13 プロジェクトに分布）。

```
$ dotnet build src/platform/backend/backend.slnx -t:Rebuild -p:NoWarn= -m:1   # 同様に knowledge も
  → 'warning xUnit1051' のログ行 1,886 / 一意化して 943
  → MSBuild のサマリ行も platform 417・knowledge 528（うち CS0618 が 2）で 943 に一致する
```

2 つの独立した数え方（サマリ行／一意化）が一致したので、943 を採る。
**`-m:1` を落とすとノード接頭辞 `N>` が付いて一意化にも失敗する**ので、数え直すときは必ず付ける。

**(2) `src/Directory.Build.props` は AST（`src/ai-stock-trading`）へ import-chain で届く。**
`IADR-0231` 決定 1 の「（`Directory.Build.props` と `Directory.Packages.props` の）**両ファイルとも
`<Import>` を持たず**」は **`Directory.Build.props` については誤り**である。

```
$ git -C src/ai-stock-trading show 9b9c676:Directory.Build.props
<Import Project="$(ParentDirectoryBuildProps)" Condition="'$(ParentDirectoryBuildProps)' != ''" />
```

`Directory.Packages.props` については同 ADR の記述は正しい（CPM は共有していない）。
結論（AST は v3 切替の対象外）は変わらないが、**本 ADR の設計はこの誤りに直接依存する**ため訂正した
（`IADR-0231` へ日付つき追記、本文の該当箇所は履歴を書き換えずに残す）。

**(3) 抑止は `WarningsAsErrors` に勝つ。**

| 条件 | 結果 |
| --- | --- |
| `WarningsAsErrors=xUnit1051` のみ | **error・ビルド失敗（exit 1）** |
| `WarningsAsErrors` ＋ `.editorconfig` の `severity = none` | **`.editorconfig` が勝つ**（診断が消える・exit 0） |
| `WarningsAsErrors` ＋ `NoWarn=xUnit1051` | **`NoWarn` が勝つ**（抑止・exit 0） |
| `.editorconfig` の `severity = none` のみ | 抑止・exit 0 |

つまり**移行済みプロジェクトへ後から抑止を足せば ratchet は黙って外れる**。

## 決定

### 決定 1: 「剥がす」の担保は `WarningsAsErrors` である（`NoWarn` を外すだけでは足りない）

`TreatWarningsAsErrors` は **`false`** である。したがって `NoWarn` を外しただけでは、
剥がしたプロジェクトで `xUnit1051` が再発しても **CI は緑のまま**になる。受け入れ基準
「剥がしたら 0 件」を機械で守るには、剥がすと同時に `WarningsAsErrors` へ入れる必要がある。

変異試験で実測した（詳細は作業仕様書）:

| 変異 | 期待 | 実測 |
| --- | --- | --- |
| 移行済みプロジェクトで `xUnit1051` を再発させる | ビルド失敗 | **`error xUnit1051` / exit 1** |
| 同じ再発を旧 props（`NoWarn` のまま）で | 素通り | **警告 0 件 / exit 0** |
| 同じ再発を「`NoWarn` を外すだけ・`WarningsAsErrors` 無し」で | 緑のまま | **warning 止まり / exit 0** |

3 行目が本決定の理由そのものである。

### 決定 2: 許可リスト（migrated を列挙）にする。拒否リストにしない

```xml
<XUnit1051Migrated>;Knowledge.Contracts.Tests;Platform.Shared.Kernel.Tests;</XUnit1051Migrated>
```

移行済みだけを挙げ、挙がったものから `NoWarn` を外して `WarningsAsErrors` を付ける。
未移行（AST・雛形を含む）は従来どおり `NoWarn` のままである。

🔴 **拒否リスト（未移行を列挙し、それ以外の `*Tests` へ `WarningsAsErrors`）を採らない理由**:

- 上の事実 (2) により、`WarningsAsErrors` は **AST の 38 本のテストプロジェクトへ届く**
  （**38 本すべてが `Tests` で終わる**）。
- AST を守っていたのは AST 自身の `.editorconfig` の `dotnet_diagnostic.xUnit1051.severity = none`
  だが、**AST はその行を後続コミットで削除している**（gitlink `9b9c676` には在り、`abce001` で消えた）。
  他リポジトリの `.editorconfig` の現状に、本リポジトリの CI の成否を依存させない。
- 雛形 `SampleService.Tests` も同じ理由で巻き込む（CI の `template-backend-build` が
  `templates/` を `src/.template-buildcheck-*/` へ複製してビルドするため props が効く）。

許可リストなら `WarningsAsErrors` は AST へ**一度も届かない**。実測でも AST の
`NoWarn` / `WarningsAsErrors` は新旧 props で**完全に同一**であった（挙動は 1 bit も変わらない）。

**許可リストの弱点は「新規テストプロジェクトが黙って `NoWarn` を継ぐ」ことである。**
これは検査器が閉じる —— 新規プロジェクトは baseline に無いので `added` で落ち、
**登録するには `remaining:0` ＋ `migrated:true` が要る**（＝新しく書くテストは最初から綺麗であることを強制する）。

### 決定 3: 残件は `scripts/xunit1051-baseline.json` を単一情報源とし、`scripts/check-xunit1051-ratchet.js` が守る

判定は 7 つ（列挙は同スクリプト冒頭を正とし、**ここへ複写しない**）。要点だけ:

- baseline ⇔ 実在プロジェクトの**双方向**（`added` / `removed`）
- baseline の `migrated` 集合 ⇔ props の `XUnit1051Migrated` の**一致**。
  🔴 MSBuild の `Contains` / `EndsWith` は**序数・大文字小文字を区別する**ため、綴りを誤った項目は
  「`NoWarn` も `WarningsAsErrors` も付かない」プロジェクトを生み、**警告は出るが CI は緑**になる。
  この一致検査がそれを止める。
- `remaining` が 0 になったら**剥がすか、剥がさない理由（`deferReason`）を書かせる**（`zero-not-locked`）
- 追跡下の `.editorconfig` / `.csproj` / `.props` への **抑止の混入**（上の事実 (3)）

**CI へは新ジョブを作らず `scripts/scripts.repo.test.js` から呼ぶ**（`IADR-0140` 決定 2 の相乗り。
`check-adr-numbering` と同じ経路で、既存の `scripts-tests` ジョブが走らせる）。
**`.github/workflows/ci.yml` は 1 行も変更していない。**

### 決定 4: 第 1 段で剥がすのは 0 件の 2 本だけ。0 件でも剥がさないものは理由を書く

| プロジェクト | 実測 | 第 1 段 | 理由 |
| --- | --- | --- | --- |
| `Knowledge.Contracts.Tests` | 0 | **剥がす** | — |
| `Platform.Shared.Kernel.Tests` | 0 | **剥がす** | — |
| `Platform.Shared.Infrastructure.Tests` | 0 | 剥がさない | U5 / Wolverine 移行チェーン（#455 系）が本プロジェクトへテストを追加中で、先に `WarningsAsErrors` を入れると並行 PR が自分の追加分で落ちる |
| `SampleService.Tests`（雛形・**17 本目**） | 1 | 剥がさない | 本 PR は器だけの約束であり、テストの `.cs` を 1 行も変えない |

**`src/` のテストの `.cs` を 1 行も変えない**ので「テスト件数が減らない」は自明に満たされ、
レビューは器の設計だけに集中できる。

第 2 段以降は **1 PR = 1 プロジェクト、実測件数の小さい順**。最初の実コード PR は
`DashboardService.Api.Tests`（**実測 24 件**）である。

## 影響

- **良い影響**
  - 「剥がしたら戻れない」が機械で保証される（再発は**ビルドが**止める）
  - 抑止の総量が baseline という 1 つの数として可視化され、単調に減る
  - AST と雛形の挙動は現状と変わらない（許可リストのため）
  - 3 つの文書に載っていた **1,886 件**という誤った数が実測 **943 件**に訂正された
- **悪い影響・トレードオフ**
  - リストが 2 箇所（props と baseline）に分かれる。**一致は検査器が強制する**が、
    MSBuild の props で JSON を読めない以上、単一ファイル化はできない
  - 検査器は**未移行プロジェクトの件数が増えたこと**を検出しない（実数にはビルドが要る）。
    未移行は `NoWarn` で抑止されているので設計どおりであり、移行済みの再発はビルドが止める
- **フォローアップ**
  - 雛形 `SampleService.Tests` の 1 件（1 行で済む）
  - `Platform.Shared.Infrastructure.Tests`（Wolverine 移行チェーンの着地後）
  - 残り 13 プロジェクト・943 件の段階移行

## 棄却した案

- **拒否リスト（未移行を列挙）** —— AST の 38 本と雛形へ `WarningsAsErrors` が届く（決定 2）
- **`NoWarn` を外すだけ** —— `TreatWarningsAsErrors` が false なので**再発しても緑**（決定 1 の実測 3 行目）
- **AST の `.editorconfig` の `severity = none` に依存する** —— AST が既にその行を削除しており、
  他リポジトリの現状に本リポジトリの CI の成否を依存させることになる
- **全 943 件を一括是正する** —— 「計画外の大規模リファクタを行わない」に反する。
  レビュー可能な単位でもない（`IADR-0231` の棄却案と同じ判断）
- **検査器で件数まで検証する** —— 実数を得るにはビルドが要り、依存ゼロ・静的という
  `scripts/` の設計方針から外れる。移行済みの再発はビルドが止めるので二重に持たない

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連する計画 ADR: `ADR-0030`（テストは xUnit v3）
- 関連する実装 ADR: `IADR-0231`（決定 4 のフォローアップ。決定 1 の `<Import>` の記述と件数を訂正した）、
  `IADR-0140`（決定 2・検査器の CI 呼び出し口）
