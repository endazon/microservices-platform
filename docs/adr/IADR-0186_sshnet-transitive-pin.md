---
title: IADR-0186 SSH.NET は推移ピンで修正版へ上げ、submodule 側は環流する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0179
author: claude
created: 2026-08-13
updated: 2026-08-13
plan_refs:
  - "../../planning/docs/ai-implementation-workflow-guide.md"
---

# IADR-0186: `SSH.NET` の推移依存ピン（#716）

- 状態: Accepted
- 日付: 2026-08-13
- 決定者: claude（実装）

## 起点・関連

- **NFR**（セキュリティ。該当する `NFR-xx` が無いため無採番。[IADR-0179](./IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）
- 実装 issue: **#716**（出所: PR #715 の CI で発覚）
- 作業仕様書: [20260813_issue-716](../specs/20260813_issue-716_sshnet-transitive-pin.md)

## 文脈 —— **差分を変えていないのに CI が赤くなった**

`dotnet list package --vulnerable --include-transitive` が **`SSH.NET 2025.1.0`**（High・GHSA-q939-rpr3-3284）を
2 プロジェクトで報告する。**`ScpClient` の再帰ダウンロードがリモート提供のファイル名を検証せず、
`../` や絶対パスでダウンロード先の外へ書き込める**という脆弱性である。

**これは時間依存の失敗である** —— 検査器は nuget.org の advisory feed を**実行時に**引くので、
**依存が 1 バイトも変わらなくても advisory 公開の瞬間から赤になる**（develop `a671a80` の CI は
2026-08-11 に緑だった）。**以後すべての PR が同じ理由で赤くなる。**

**`SSH.NET` は誰も直接参照していない**（`git grep` で `*.csproj` / `*.props` に 0 件）。
**`Testcontainers` の推移依存**である。

## ★ 決定 1: **`SSH.NET` を `2026.0.0` へ推移ピンする（#61 と同手順）**

`src/Directory.Packages.props` は **`CentralPackageTransitivePinningEnabled` を既に true** にしており、
**同型の先例がある** —— `Microsoft.OpenApi` を推移ピンして NU1903 を解消した（#61 / #80）。**同じ形を採る。**

### なぜ上流の更新ではないのか（実測）

**`Testcontainers` を上げても直らない。** nuspec を実測した。

| Testcontainers | `SSH.NET` の依存 |
| --- | --- |
| **4.12.0**（本リポの現行） | **2025.1.0** |
| **4.13.0**（最新） | **2025.1.0**（**据え置き**） |

**上流が上げていないので、推移ピンが唯一の手段である。**
**あわせて `Testcontainers` は上げない** —— 上げても直らず、**直らない変更を混ぜると赤の原因が分からなくなる。**

## ★★ 決定 2: **破壊的変更を踏まないことを、アセンブリを実測して確かめた**

**`SSH.NET 2026.0.0` はメジャー版が上がる。** リリースノートは「Breaking Changes: None known」と書くが、
**セキュリティ修正そのものが `ScpClient` の面を変えている** ——
**「Require an explicit `IRemotePathTransformation` for `ScpClient`」**。

**「たぶん使っていない」で済ませず、配布アセンブリの型参照を実測した。**

```
$ strings lib/net10.0/Testcontainers.dll | grep -oE 'ScpClient|SshClient|ForwardedPortRemote|Renci\.SshNet'
SshClient
ForwardedPortRemote
Renci.SshNet
```

**`ScpClient` は現れない。** `Testcontainers` は **SSH ポートフォワード**にしか `SSH.NET` を使っておらず、
**破壊的変更のある唯一の型を踏まない。**

**TFM も後退しない。**

| | targetFrameworks |
| --- | --- |
| `2025.1.0` | net462 / netstandard2.0 / net8.0 / net9.0 |
| **`2026.0.0`** | net462 / netstandard2.0 / net8.0 / net9.0 / **net10.0（追加）** |

**本リポは net10.0 である** —— 2026.0.0 は**ネイティブな net10.0 資産を持つ**ので、**互換性はむしろ改善する。**

## ★★ 決定 3: **submodule 側（AST）は直さない。環流する —— 本 PR で赤は消えない**

**これが本決定で最も誤解を招きやすい点なので明記する。**

`dotnet list package --vulnerable` は **2 プロジェクト**を報告した。

| プロジェクト | 所属 | 本 PR のピンが効くか |
| --- | --- | ---: |
| `Knowledge.IntegrationTests` | **本リポ** | **効く** |
| `AiStockTrading.IntegrationTests` | **submodule `src/ai-stock-trading`** | **効かない** |

**MSBuild は各プロジェクトから上へ辿って最初に見つけた `Directory.Packages.props` を使う。**
**AST は自前の `Directory.Packages.props` を持つ**（実測。`CentralPackageTransitivePinningEnabled` も true、
`Testcontainers 4.13.0`）ので、**`src/Directory.Packages.props` への追記は AST に届かない。**

**かつ `CLAUDE.md` は `src/ai-stock-trading` の変更を禁じている**（別プロジェクトの submodule）。
**AST のソリューションは `security.yml` の `find -maxdepth 4` に入る**ので、**走査からも外れない。**

> **★ したがって「#716 でジョブが緑になる」とは書けない。消えるのは 2 件中 1 件である。**
> **受け入れ基準の側を実測に合わせる** —— **満たすために事実を歪めない**（#710 / [IADR-0184](./IADR-0184_feedback-dispatch-checker-verbatim.md) 決定 2 と同じ作法）。

**AST 側は #722 として起票済みである。**

## 決定 4: **ピンの存在を回帰テストで固定する（同型 2 回目）**

`CLAUDE.md` は**「検査器・規約の追加は同型の事故が 2 回起きたら」**と定める。
**推移依存の脆弱性ピンは #61（`Microsoft.OpenApi`）に次いで 2 回目**である。

**ピンが黙って消えると脆弱性が再混入する**（かつ**再混入は advisory feed 次第で気づくのが遅れる**）ので、
**ピンの存在と下限を回帰テストで固定する。**

## 結果

- 良い影響
  - **本リポの `Knowledge.IntegrationTests` から High の脆弱性が消える**
  - **net10.0 のネイティブ資産**を使うようになる（従来は net9.0 資産へのフォールバック）
  - **ピンの消失を回帰テストが止める**
- 悪い影響・トレードオフ
  - **`Vulnerable transitive dependencies` は緑にならない**（決定 3）。**AST 側が直るまで赤のまま**であり、
    **その間はこのジョブが「読まれない赤」になり続ける**というコストを負う
  - **手元に .NET SDK が無く、ビルド・統合テストを実走できていない**（後述）
- フォローアップ
  - **AST 側の同一脆弱性は #722**
  - **`security.yml` が submodule を走査対象に含めることの是非**は別途（**本リポで直せない赤を出し続ける**構造である）
  - **本作業中に別の develop 由来の失敗を検出した** —— **pin `cff0e7b` へのキット追随漏れ**で
    `scripts.test.js` が `planning` populate 時に 107/494 で停止する（**#721**）。
    **本決定とは無関係**だが、**`planning` を populate して走らせなければ気づけなかった**
    （[IADR-0185](./IADR-0185_feedback-status-vocabulary.md) の追記で得た教訓を適用した結果である）

## ★ 検証の限界

**本作業の環境に .NET SDK が無い**（`dotnet: command not found`）。
**`dotnet list package --vulnerable` / `dotnet build` / `dotnet test` を手元で実走していない。**

| 確かめたこと | 手段 |
| --- | --- |
| `SSH.NET 2026.0.0` の実在・TFM | **nuget.org flatcontainer API を実測** |
| `Testcontainers` の `SSH.NET` 依存版（4.12.0 / 4.13.0） | **nuspec を実測** |
| `Testcontainers` が `ScpClient` を使わないこと | **配布アセンブリの型参照を実測** |
| **ビルド・統合テストが通ること** | **CI の `build-and-test` に委ねる** |

**「手元で緑」を主張しない。**

## 関連

- Supersedes: なし
- Superseded by: なし
