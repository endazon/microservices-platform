---
title: IADR-0224 雛形 backend は「配置後の位置への一時複製」をビルド・テストして CI で検証する（templates/ 自体は依然どの slnx にも登録しない）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-14
  - IADR-0060
  - IADR-0209
author: claude
created: 2026-08-18
updated: 2026-08-18
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR 表 NFR-01..27。当たる番号が無いことの確認先)"
  - "../../planning/docs/ai-implementation-workflow-guide.md (§フェーズ末監査・§11 複数実装リポのパリティ)"
related_specs:
  - "../specs/20260818_issue-830_template-backend-ci-build.md"
  - "../specs/20260818_wave12-audit-followup.md"
  - "./IADR-0060_submodule-unit-operations.md"
  - "./IADR-0209_vitest-include-subset-of-frontend-tests-paths.md"
---

# IADR-0224: 雛形 backend を一時複製のビルド・テストで検証する（#830）

- 状態: Accepted
- 日付: 2026-08-18
- 決定者: claude（実装）

## 起点・関連

- **NFR**（雛形の健全性を CI で守る＝**メタ作業**。計画の非機能要件表 `NFR-01`〜`NFR-27` は 27 件とも
  **稼働する製品**の要件であり、当たる番号が無い。`.claude/rules/traceability.md`「無採番 `NFR` を許す
  2 つの場合」の**場合 2** に当たるため無採番とし、**環流しない**。[[IADR-0179]] 決定 2）
- 実装 issue: **#830**（`template-backend-build` ジョブの新設）
- 作業仕様書: [20260818_issue-830](../specs/20260818_issue-830_template-backend-ci-build.md) ／
  本 ADR の起票経緯は [20260818_wave12-audit-followup](../specs/20260818_wave12-audit-followup.md)
- 先行する同型例: [[IADR-0209]]（雛形 `templates/*/frontend/**` を `frontend-tests.yml` の
  `paths:` へ入れ、CI の対象にした。#801）

## コンテキストと課題

[[IADR-0060]] 決定 3 は「テンプレートは本リポジトリの**ビルド対象ではない**
（`src/` 外・どの slnx にも含めない）」と定める。この位置づけの下では、雛形 backend が壊れても
**新ユニットを実際に起こすまで誰も気付けない**。

**#830 は雛形 backend を CI でビルド・テストする。** 括弧内の 2 条件（`src/` 外・どの slnx にも
含めない）は保たれるが、**「ビルド対象ではない」という位置づけそのものは実質的に変わる。**
**決定の記録が無いまま位置づけを変えない**ため、本 ADR で記録する。

雛形は `templates/` の位置のままではビルドできない（実測）:

- `SampleService.Api.csproj` の platform `Shared` への相対参照（`..\` × 6）は**配置後**の
  `src/<unit>/backend/...` を前提とする。`templates/` 位置では `templates/platform/...` を探して
  `MSB4181` で restore が失敗する。
- 共通 props は `.sample` 付きで配布される（配置時に `src/Directory.Build.props` /
  `.Packages.props` を継承させ、ユニット側に常設 props を置かないため。[[IADR-0060]] 決定 4）。

## 検討した選択肢

| 案 | 評価 |
| --- | --- |
| **A. 配置後の位置（`src/<一時ユニット>/backend/`）へ複製してビルド・テストする** | **採用。** 実際の配置と同じ相対参照・同じ共通 props 継承で検証できる唯一の形 |
| B. `templates/` の位置のままビルドする | **不可**。上記のとおり `MSB4181` で restore が落ちる。雛形側を「その場でビルドできる」形に変えると、配置後の形と乖離して検査の意味が失われる |
| C. 雛形を `src/` 配下の常設ユニットとして持つ | [[IADR-0060]] 決定 3 の括弧内 2 条件を実際に破る。雛形が本番ユニットとして扱われ、依存やパッケージ更新の巻き添えを受ける |
| D. `build-and-test` ジョブへ相乗りする | **不採用**（下記 決定 6） |

## 決定

### 1. 配置後の位置への一時複製をビルド・テストする

`templates/*/backend/backend.slnx` を `src/.template-buildcheck-<name>/backend` へ複製し、
そこで `dotnet build` / `dotnet test` を走らせる。**`templates/` 自体は依然としてどの slnx にも
登録しない。ビルドするのは一時的な複製であって `templates/` ではない**（[[IADR-0060]] 決定 3 の
括弧内 2 条件は不変）。`trap` で必ず片付け、後段ステップ（`if: always()`）が
`git status --short --ignored -- src/` で**残骸ゼロを実測**する。

### 2. `.sample` は複製先へ置かない

`.sample` は「単独リポジトリでビルドするときだけ」置くもの（[[IADR-0060]] 決定 4）。配置後を模す
本ジョブでは置かない —— **置くと `src/` の単一情報源より近い階層で発見され、上書きしてしまう。**

### 3. `--artifacts-path` で `obj` / `bin` を作業ツリーの外へ逃がす

これが無いと `ProjectReference` 先の `src/platform/backend/Shared/*/{bin,obj}` まで作業ツリーへ
生え、`src/` が汚れる（#830 受け入れ基準）。複製前にも作業ツリー由来の `bin` / `obj` を除去し、
前回の生成物を複製へ持ち込まない。

### ★★ 4. 判定は「終了コード」ではなく**実行件数の下限**で行う

`dotnet test` の終了コードと `Test Run Successful` だけを見ると、**0 件実行や 1 件取りこぼしを
緑と読む**（`Skip` を緑と読む形も同じ）。そこで `--verbosity normal` で実行されたテスト名をログへ出し、

```
expected = 行頭（インデントのみ）の [Fact] / [Theory] の数
executed = ログの Passed 行の数
executed < expected なら fail / expected == 0 でも fail（雛形のテストが消えている）
```

で判定する。`[Theory]` は `InlineData` で件数が増えうるため**下限**で判定する。属性は行頭に限って
数え、注釈中の `[Fact]` という文字列を拾わない。

### ★★ 5. 既知の前提: **件数下限の判定はテスト名が ASCII で始まることを前提にする**

`executed` の抽出は `grep -cE '^[[:space:]]+Passed[[:space:]]+[A-Za-z]'` である。
**先頭が日本語（非 ASCII）のテストを雛形へ足すと、`[A-Za-z]` に当たらず `executed=0` となり、
「実行件数 0 が `[Fact]`/`[Theory]` の N を下回った」という誤ったメッセージで落ちる。**
テストは実際には通っているため、原因の特定に時間がかかる形である。

**雛形へテストを増やすときの前提として記録する。** 日本語のテスト名を雛形へ入れる必要が生じたら、
この抽出条件を先に直すこと（本リポの `src/` 側のテストは対象外。本ジョブが読むのは
`templates/*/backend/` のログだけである）。

**追跡先: #865。** 記録だけでは、雛形にテストを足す人がこの条文を読むとは限らないため、
是正（正規表現の Unicode 対応 / TRX 等の構造化出力への切り替え / 注記の強化）を issue で追う。
**`Skip` を緑と読まない性質は、この判定の存在理由なので壊さないこと**（決定 4）。

> **［2026-08-18 追記 / #865］上の決定 5 の前提は誤りである。本文は当時の記録として残す。**
>
> **「先頭が日本語のテストを足すと `executed=0` になる」は、そのままでは再現しない。**
> `dotnet test --verbosity normal` がログへ出すのは**完全修飾名**（`名前空間.型.メソッド`）であり、
> `[A-Za-z]` が見ているのは**メソッド名の頭ではなく名前空間の頭**である。実測:
>
> ```console
> $ dotnet test ... --nologo -v n | grep -E '^\s+Passed\s'
>   Passed LlmGateway.Api.Tests.LlmFallbackPolicyTests.ShouldNotFallBack_OnRateLimit429 [1 ms]
> ```
>
> 雛形へ先頭が日本語のテストを実際に足して測っても、旧パターンは **3 件すべてを数える**。
>
> **正しい発火条件は「名前空間または型が非 ASCII で始まるとき」であり、そのときの値は `0` ではなく `1`**
> （ASCII 始まりの完全修飾名が残るため）。**危険度は下がらないが、部分的に数えるぶん原因はより分かりにくい。**
>
> **追跡先 #865 は「MSP の仕様と関係しないメタ作業」として利用者判断でクローズした**（2026-08-18）。
> 是正が要るときは**文字集合を数え上げない**こと —— `[:alpha:]` を足しても次の言語で穴が開く。
> 行の構造で切る（`Passed` の直後に空白、その次が非空白）。境界は二重に効く ——
> `␣␣␣␣␣Passed: 3` は直後が `:`、`Passed!  - Failed: …` は行頭に空白が無い。
> **`Skip` を緑と読まない性質（決定 4 の存在理由）は、この形でも壊れない**（`Skipped` 行は `Passed` を含まない）。

### 6. `dotnet format` は本ジョブに含めない。`build-and-test` へも相乗りしない

- **`dotnet format` を含めない**: `--artifacts-path` を解さず（`--no-restore` を付けても）
  `src/platform/backend/Shared/*/obj` を作業ツリーへ書き出し、決定 3 の「`src/` を汚さない」を破る。
  **`lint` 側に同型の穴（雛形に当たらない）が残る**が、本 ADR の射程外として別 issue へ回す。
- **`build-and-test` へ相乗りしない**: 同ジョブは `src/*/backend/backend.slnx` の glob を回す。
  複製は一時的に `src/` 配下へ現れるため、相乗りさせると**glob の対象と複製の生成が同一ジョブ内で
  競合**し、順序に依存する。加えて**失敗がジョブ名で見えなくなる**（雛形が壊れたのか本体が壊れたのかを
  チェック名で区別できない）。**新設のジョブ ID とし、既存の必須チェック名
  （`build-and-test` / `lint` / `commit-messages`）は 1 つも変えていない**（`docs/ai-workflow.md` の表）。

## 理由

- 雛形の壊れは「**新ユニットを起こすとき**」という最も遅い時点でしか露見しない。配置後の形で
  ビルド・テストすることが、その時点を CI まで前倒しする唯一の方法である。
- 決定 4 は本リポが繰り返し踏んできた「**検査していないことを違反 0 件と読む**」事故の予防である
  （[[IADR-0130]] 系の作法と同型）。
- [[IADR-0060]] 決定 3 の括弧内 2 条件を保ったまま位置づけだけを変えるため、**改定 ADR ではなく
  新規 ADR ＋ 旧 ADR への追記**という形を採る。

## 結果

- 良い影響: 雛形 backend の restore / build / test の破れが CI で止まる。`.sample` の扱い・
  相対参照の前提が実際に検証される。
- 悪い影響・トレードオフ:
  - CI に .NET SDK のセットアップを行うジョブが 1 本増える（実行時間の増加）。
  - **決定 5 の ASCII 前提**という、雛形側の書き方に対する暗黙の制約が生じる。
  - `templates/` は「ビルド対象ではない」と書かれた文書が複数あり、**読み手には位置づけが
    分かりにくくなる**。[[IADR-0060]] へ追記して導線を張る。
- フォローアップ:
  - `lint`（`dotnet format`）が雛形に当たらない同型の穴 —— 別 issue へ。
  - `templates/unit-template/README.md` から本ジョブへの導線 —— 別 issue へ。

## 関連

- Supersedes: なし
- Superseded by: なし
- [[IADR-0060]] 決定 3 の位置づけを本 ADR が変える（同 ADR に日付つき追記あり。**本文は不変**）
- [[IADR-0209]]（雛形 frontend を CI 対象に入れた先行例。#801）
