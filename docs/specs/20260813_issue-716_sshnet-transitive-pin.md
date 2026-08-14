---
title: 作業仕様書 — SSH.NET の推移依存を修正版へピンする（#716）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0186
author: claude
created: 2026-08-13
updated: 2026-08-13
plan_refs:
  - "../../planning/docs/ai-implementation-workflow-guide.md (§6 裁定は小さく高頻度に流す)"
related_specs:
  - "../adr/IADR-0186_sshnet-transitive-pin.md"
---

# 作業仕様書: `SSH.NET` の推移依存ピン（#716）

## 起点

- **NFR**（セキュリティ。**メタ作業ではなく製品の依存**だが、該当する `NFR-xx` は無いため無採番。[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）
- 起点 issue: **#716**。実装 ADR: **[IADR-0186](../adr/IADR-0186_sshnet-transitive-pin.md)**
- 出所: **PR #715 の CI** で `Vulnerable transitive dependencies` が赤になり、**本 PR の差分と無関係**と実測で確定した分

> **★ 値の基準時点は develop `51467cc`（2026-08-13 実測）である。**

## 事象

`dotnet list package --vulnerable --include-transitive` が **`SSH.NET 2025.1.0`** を High で報告する。

```
Project `Knowledge.IntegrationTests`      > SSH.NET 2025.1.0 High GHSA-q939-rpr3-3284
Project `AiStockTrading.IntegrationTests` > SSH.NET 2025.1.0 High GHSA-q939-rpr3-3284
```

**時間依存の失敗である** —— 検査器は nuget.org の advisory feed を実行時に引くので、
**依存が 1 バイトも変わらなくても advisory 公開の瞬間から赤になる**（develop `a671a80` の CI は 2026-08-11 に緑）。

## ★★ 母集合 —— 実測で引いた

### 軸 a: **`SSH.NET` は誰も直接参照していない**

| 確かめたこと | 結果 |
| --- | --- |
| `src/Directory.Packages.props` に `SSH.NET` | **無い** |
| 追跡下の `*.csproj` / `*.props` に `SSH.NET` | **0 件**（`git grep`） |
| `Testcontainers` を参照するプロジェクト | **`Knowledge.IntegrationTests` の 1 つだけ** |

**`Testcontainers` の推移依存である。**

### 軸 b: **上流の更新では直らない**

**`Testcontainers` を上げても解決しない。** nuspec を実測した。

| Testcontainers | `SSH.NET` の依存 |
| --- | --- |
| **4.12.0**（本リポの現行） | **2025.1.0** |
| **4.13.0**（最新） | **2025.1.0**（**据え置き**） |

**→ 上流が上げていないので、`CentralPackageTransitivePinningEnabled` による推移ピンが唯一の手段である。**

### ★★ 軸 c: **`ScpClient` の破壊的変更は本リポに当たらない**

`SSH.NET 2026.0.0` は**修正版**だが、リリースノートに
**「Require an explicit `IRemotePathTransformation` for `ScpClient`」**があり、`ScpClient` の面が変わる。

**`Testcontainers` がその面を使っていないことを、配布アセンブリを実測して確かめた。**

```
$ strings lib/net10.0/Testcontainers.dll | grep -oE 'ScpClient|SshClient|ForwardedPortRemote|Renci\.SshNet'
SshClient
ForwardedPortRemote
Renci.SshNet
```

**`ScpClient` は現れない** —— `Testcontainers` は **SSH ポートフォワード**（`SshClient` / `ForwardedPortRemote`）にしか使っていない。
**破壊的変更のある唯一の型を踏まない。**

### 軸 d: **TFM は後退しない（むしろ前進する）**

| | targetFrameworks |
| --- | --- |
| `SSH.NET 2025.1.0` | net462 / netstandard2.0 / **net8.0 / net9.0** |
| **`SSH.NET 2026.0.0`** | net462 / netstandard2.0 / net8.0 / net9.0 / **net10.0（追加）** |

**本リポは net10.0 である。** 2026.0.0 は**ネイティブな net10.0 資産を持つ**ので、
**互換性の観点はむしろ改善する**（従来は net9.0 資産へフォールバックしていた）。

### ★★ 軸 e: **submodule 側（AST）には効かない —— 本 PR では赤が消えない**

**これが本作業の最重要の実測である。**

| 確かめたこと | 結果 |
| --- | --- |
| `src/ai-stock-trading/Directory.Packages.props` | **存在する**（AST 自身が持つ） |
| 同ファイルの `CentralPackageTransitivePinningEnabled` | **true** |
| 同ファイルの `Testcontainers` | **4.13.0**（＝ `SSH.NET 2025.1.0` を引く） |
| AST のソリューション | `src/ai-stock-trading/backend/backend.slnx`（ルートから**深さ 4**） |
| `security.yml` の走査 | `find . -maxdepth 4 \( -name '*.slnx' -o -name '*.sln' \)` → **AST も走査対象** |

**MSBuild は各プロジェクトから上へ辿って最初に見つけた `Directory.Packages.props` を使う。**
**AST は自前を持つので、`src/Directory.Packages.props` への追記は AST に届かない。**

**かつ `CLAUDE.md` は `src/ai-stock-trading` の変更を禁じている**（別プロジェクトの submodule）。

**→ 本 PR で `Vulnerable transitive dependencies` は緑にならない。** AST 側の是正が要る。

## 判断

### 判断 1: **`SSH.NET` を `2026.0.0` へ推移ピンする（#61 と同手順）**

`src/Directory.Packages.props` は **`CentralPackageTransitivePinningEnabled` を既に true** にしており、
**同型の先例がある** —— `Microsoft.OpenApi` を推移ピンで NU1903 を解消した（#61 / #80）。**同じ形を採る。**

### 判断 2: **`Testcontainers` は上げない**

**上げても直らない**（軸 b）。**直らない変更を混ぜると、赤の原因が分からなくなる。**
`4.12.0` → `4.13.0` は**本 issue と無関係な変更**であり、別途の判断とする。

### ★ 判断 3: **AST 側は本 PR で直さない。環流する**

**規約で変更が禁じられており**（`CLAUDE.md`）、**そもそも別リポジトリの採番・依存方針**である。
**本 PR は MSP 側だけを直し、AST 側は #722 として起票済みである。**

> **★ したがって「#716 で赤が消える」とは書けない。** **消えるのは 2 件中 1 件**である。
> **受け入れ基準の側を実測に合わせる**（#710 / [IADR-0184](../adr/IADR-0184_feedback-dispatch-checker-verbatim.md) と同じ作法 —— **満たすために事実を歪めない**）。

### 判断 4: **回帰テストを 1 本置く（同型 2 回目）**

`CLAUDE.md` は**「検査器・規約の追加は同型の事故が 2 回起きたら」**と定める。
**推移依存の脆弱性ピンは #61（`Microsoft.OpenApi`）に次いで 2 回目**である。
**ピンが黙って消えると脆弱性が再混入する**ので、**ピンの存在と下限を回帰テストで固定する。**

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（#716） | 確かめ方 |
| --- | --- | --- |
| 1 | `SSH.NET` が `2026.0.0` へピンされている | **回帰テストで固定**（下限つき） |
| 2 | `Testcontainers` が `2026.0.0` と互換か | **配布アセンブリを実測**（`ScpClient` 不使用・軸 c）／**CI の build-and-test が実走で確認** |
| 3 | `dotnet list package --vulnerable` が 0 件 | **MSP 側のみ 0 件**（AST 側は判断 3・#722） |
| 4 | 統合テストが通る | **CI の `build-and-test`**（**手元に .NET SDK が無く実走できない** —— 後述） |
| 5 | submodule 側の要否 | **要る。届かないことを実測**（軸 e）→ **#722** |

## ★ 検証の限界（正直に書く）

**本セッションの環境に .NET SDK が無い**（`dotnet: command not found`）。
**したがって `dotnet list package --vulnerable` / `dotnet build` / `dotnet test` を手元で実走できていない。**

| 確かめたこと | 手段 |
| --- | --- |
| `SSH.NET 2026.0.0` の実在・TFM・依存 | **nuget.org の flatcontainer API を実測** |
| `Testcontainers` の `SSH.NET` 依存版 | **nuspec を実測**（4.12.0 / 4.13.0 とも 2025.1.0） |
| `Testcontainers` が `ScpClient` を使わないこと | **配布アセンブリの型参照を実測** |
| **ビルド・統合テストが通ること** | **CI に委ねる**（`build-and-test` ジョブ） |

**「手元で緑」を主張しない。** CI の `build-and-test` が実走の唯一の証拠である。

## 着地の実測

| | 値 |
| --- | --- |
| `src/Directory.Packages.props` | **`SSH.NET 2026.0.0` を 1 行追加**（推移ピン） |
| `scripts.test.js`（`planning` **未** populate） | **491 → 494 件**（回帰テスト **3 件**を追加・全数 pass） |
| 変異試験 | **6 変異すべてを検出**（N1〜N6。原状復帰つき） |
| `check-doc-links` ほか文書系 7 本 | **すべて OK** |

### ★★ `planning` を populate すると **develop 由来の別の失敗**で停止する（本 PR 起因ではない）

**PR #715 のレビューの教訓に従い、`planning` を pin どおり populate して走らせたところ、
`scripts.test.js` が 494 件中 107 件で停止した。** **clean な develop（`51467cc`）で再現する。**

| | |
| --- | --- |
| 失敗 | `キットとバイト一致でなくなった（IADR-0115 決定 2）` |
| 原因 | **#718 が planning pin を `2cf0795` → `cff0e7b` へ進めたが、キット追随を伴っていない**（planning#319 / planning#323 が反映済み） |
| 乖離 | `scripts/check-feedback-dispatched.js` **536 行** / `feedback/README.md` **101 行** |
| なぜ CI で見えないか | `scripts-tests` が `planning` を populate しないため**比較が skip される** |
| 行き先 | **#721** |

**本 PR は `src/Directory.Packages.props` と `scripts/scripts.repo.test.js` の追記しか触っておらず、
この失敗とは無関係である**（stash して clean develop で再現済み）。
**したがって本 PR の主張は「`planning` 未 populate で 494 件 pass」までとし、
「populate でも全数 pass」とは書かない** —— **#721 が解消するまで成立しない。**

## 射程外

- **AST 側（`src/ai-stock-trading`）の同一脆弱性** —— **#722**（規約で変更禁止）
- **`Testcontainers` 4.12.0 → 4.13.0 の更新** —— 本 issue と無関係（判断 2）
- **`security.yml` の走査範囲（AST を含めるか）の見直し** —— **#722** で天秤として扱う
- **pin `cff0e7b` へのキット追随漏れ**（`check-feedback-dispatched.js` / `feedback/README.md`）—— **#721**
