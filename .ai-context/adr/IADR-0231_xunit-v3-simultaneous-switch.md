---
title: IADR-0231 xUnit を v2 から v3 へ 16 プロジェクト一斉に切り替え、版整合の検査を対称化する
type: impl-adr
status: Accepted
related_ids:
  - ADR-0030
  - IADR-0229
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (テスト = xUnit v3)
---

# IADR-0231 xUnit を v2 から v3 へ 16 プロジェクト一斉に切り替え、版整合の検査を対称化する

## 状況

計画 `ADR-0030` のテスト標準は **xUnit v3** だが、実装は v2 のまま出荷していた。
`IADR-0229` 決定 5 が「切替 issue の完了まで v2 を使う」と**条件付きで**据え置いていた。

据え置きの理由は CPM の構造にある。`xunit.runner.visualstudio` は **v2 用（2.x）と v3 用（3.x）で
別系列**であり、**CPM は 1 パッケージに 1 バージョンしか持てない**。runner を 3.x へ上げた瞬間に
v2 のままのプロジェクトは非互換の runner と組み合わさる。**段階移行が構造的に成立しない。**

## 決定

### 決定 1: 16 プロジェクトを同時に切り替える

`Microsoft.NET.Test.Sdk` を参照する `.csproj` **16 件**（knowledge 11 / platform 4 / 雛形 1）を
一度に `xunit` → `xunit.v3` へ移し、CPM の runner を **2.8.2 → 3.1.5** へ上げた。
`xunit`（v2 本体）と `Xunit.SkippableFact` の `PackageVersion` は参照が 0 件になったので削除した。

**`src/ai-stock-trading`（AST submodule）は対象外である。** 自前の `Directory.Packages.props` を持ち
本リポと CPM を共有しない（両ファイルとも `<Import>` を持たず、`DirectoryPackagesPropsPath` の
上書きもリポジトリ全体で 0 件。MSBuild は最も近い祖先だけを使う）。AST は先に v3 へ移行済みで、
**本切替の参照実装として読めた**（csproj の形・`IAsyncLifetime` の `ValueTask` 化がそのまま使えた）。

### 決定 2: 版整合の検査を**対称**にする

`check-backend-libraries.js` の `xunitRunnerMismatch` は当初「`xunit.v3` 参照 ＋ runner 2.x」の
**一方向**しか見ていなかった。runner が 2.x に固定されていた時代はそれで十分だったが、
本切替で runner を 3.x へ上げた結果、**逆向きの取り残し**——`xunit`（v2 本体）を参照したままの
プロジェクト——が同じく非互換になるのに検出されなくなる。

**これは新しい検査器の追加ではなく、既存の検査に欠けていた対称な半分である**
（「検査器の追加は同型の事故が 2 回起きたら」の対象外）。**一斉切替でしか成立しないという性質
そのもの**を、以後は機械が担保する。変異試験で両方向とも fail することを実測した。

### 決定 3: 動的スキップは `Assert.Skip*` に統一し、ソフトスキップを撲滅する

`Xunit.SkippableFact` は **v3 対応版が存在しない**（最新 1.5.61 も `xunit.extensibility.execution`
v2 に依存する）。v3 標準の `Assert.Skip` / `Assert.SkipUnless` / `Assert.SkipWhen` へ移した。

🔴 **v2 のうちに外してはならなかった。** xUnit v2 には動的スキップが無く、先に外すと
**「真の Skipped」が「何もしない Passed」へ退化する**。`Assert.Skip` が在る本作業が正しい置き場である。

同じ理由で、**`if (cond) return;` のソフトスキップも撲滅した**（`PandocConversionServiceTests` の
3 箇所）。🔴 **CI に pandoc は入っていない。従前この 2 ケースは毎回 Passed と報告されていたが、
本体は 1 行も実行されていなかった。** 切替後は Skipped として現れる。

### 決定 4: アナライザ `xUnit1051` はテストプロジェクトのみ抑止し、採用は別作業とする

v3 のアナライザは「`CancellationToken` を受ける呼び出しには `TestContext.Current.CancellationToken`
を渡せ」と勧告する。切替直後の本リポジトリでは **1,886 件**発生する。

`src/Directory.Build.props` でテストプロジェクトのみ `NoWarn` する。**「面倒だから」ではない** ——
本リポジトリは `check-backend-libraries.js` に「**赤の常態化は『赤を無視する学習』を生み、検査の
目的そのものを壊す**」と記録している。1,886 件の助言警告を出し続ければ、同じ理由で `CS0618` の
ような実害のある警告が埋もれる。**段階採用へ回すのが同じ判断である。**

`TreatWarningsAsErrors` は `false` であり、この抑止はビルドの成否を変えない。
🔴 **この抑止は恒久ではない。** 採用は **#882** で ratchet により段階的に行い、完了時に `NoWarn` を削除する。

なお **`xUnit3003`**（`FactAttribute` 派生は呼び出し元のソース位置を受け取るべき）は 1 ファイル
（`DockerFactAttribute`）だけなので**抑止せず直した**。抑止と是正の線は件数と改修範囲で引いている。

## 影響

- **良い影響**
  - 計画 `ADR-0030` のテスト標準と実装が一致した。`IADR-0229` 決定 5 の条件が満たされた
  - **走っていないのに緑だったテストが 2 件、正直に Skipped として現れるようになった**
  - 依存が 2 パッケージ減る（`xunit` / `Xunit.SkippableFact`）
  - 「一斉でなければ壊れる」性質が機械検査になった（決定 2）
- **悪い影響・トレードオフ**
  - `xUnit1051` を抑止したぶん、キャンセル応答性の助言は当面働かない（決定 4。別 issue）
  - `Knowledge.IntegrationTests` の 9 ファイルが `ValueTask` へ変わった（v3 の破壊的変更）
- **フォローアップ**
  - `TestContext.Current.CancellationToken` の段階採用（**#882**）

## 棄却した案

- **段階移行（サービス単位で v3 へ）** —— CPM が 1 パッケージ 1 バージョンしか持てないため
  **成立しない**。runner を上げた時点で未移行のプロジェクトが壊れる
- **`Xunit.SkippableFact` を v2 のうちに外す** —— v2 に動的スキップが無く、
  真の Skipped が no-op の Passed へ退化する（決定 3）
- **`xUnit1051` を全件是正する** —— 1,886 箇所の呼び出し側書き換えであり、
  「計画外の大規模リファクタを行わない」に反する。別 issue の段階採用が適切である
- **`xUnit1051` を放置する** —— 実害のある警告が埋もれる（決定 4 の理由そのもの）

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連する計画 ADR: `ADR-0030`（アプリケーション層ライブラリ標準・テストは xUnit v3）
- 関連する実装 ADR: `IADR-0229`（決定 5 の条件を満たした）
