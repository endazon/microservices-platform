---
title: 作業仕様書 — analysis 用途から claude-fable-5 を外し claude-opus-5 へ寄せる（#850 / 計画 ADR-0038 への追随）
type: spec
status: done
related_ids:
  - ADR-0038
  - ADR-0010
  - ADR-0025
  - FR-11
  - UC-02
  - IADR-0022
  - IADR-0112
author: claude
created: 2026-08-18
updated: 2026-08-18
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0038_analysis-purpose-drop-fable-5.md (Accepted)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (Accepted・部分改定される側)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md (Accepted・部分改定される側)"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md"
related_specs:
  - ./20260706_ADR-0010_default-model-and-fable5-copilot.md
  - ./20260730_issue-420-421_report-and-trade-model-routing.md
  - ./20260731_issue-309_report-monthly-zdr-model.md
---

# 作業仕様書: `analysis` 用途から `claude-fable-5` を外す（#850）

## 1. 起点となる計画書（トレーサビリティ）

- 計画 ADR: `ADR-0038`（`Accepted`・利用者裁定／質問票 第 10 回 Q1・planning#83）。
  部分改定される側は `ADR-0010`（最難関＝`claude-fable-5`）と `ADR-0025`（他層は変更しない）。
- 機能要求: `FR-11`（LLM 送信可否とルーティング）／ユースケース: `UC-02`。
- 実装 ADR: [[IADR-0022]]（`analysis` → `claude-fable-5` と `NonZdrModels` の導入）、
  [[IADR-0112]]（報告書の種別別割当・`NonZdrModels` は変更しないとした決定 2）、
  [[IADR-0113]]（月報のみ ZDR 対応の最上位へ改定）。
- 起点 issue: [#850](https://github.com/endazon/microservices-platform/issues/850)。
  計画側の検出経路は planning#392（週次棚卸し）／planning#394（人手の突合）。

計画 ADR-0038 の決定のうち、本作業が実装へ落とすのは**決定 1・決定 2** である。

1. `analysis` の割当を `claude-fable-5` → `claude-opus-5` へ改定する。
2. `claude-fable-5` を利用許可集合（`claude-managed` の `Models`）から外す。基盤のいかなる用途でも用いない。

## 2. 対象範囲

- 対象: `src/platform/backend/Services/LlmGateway/**`、`docs/adr/IADR-0022*`、`docs/adr/IADR-0112*`、
  **`docs/adr/IADR-0113*`・`docs/adr/IADR-0114*`・`docs/functional/FR-11_llm-egress-routing.md`・
  `docs/tests/FR-11_llm-egress-routing.md`・`deploy/docker-compose.yml`**、本仕様書。
- 対象外（理由つきで §3.3 に列挙）: `docs/adr/README.md` / `scripts/` / `.github/workflows/`。

> **［2026-08-18 追記 / #850］作業途中で対象範囲を広げた。**
> 当初の領域は `IADR-0022` / `IADR-0112` に限られており、`IADR-0113`・`docs/functional/`・`docs/tests/` は
> §6・§7 に「要追随だが領域外」と書いて止めていた。**並行レーン 2 本がいずれもこれらを触っていないことが
> 確認され、衝突しないため広げる判断が下りた**（`docs/adr/README.md` の索引行だけは引き続き触らない）。
> **理由は「後回しにできないため」である** —— 条文が実装と食い違ったまま残ると、後続の監査が
> 未達を解消済みと読む。以下、§3.3・§4.2・§6・§7 は広げた後の内容へ書き改めてある
> （`docs/specs/` の凍結は**確定済み＝過去 PR の**仕様書が対象であり、
> **作業中の PR の仕様書は別**である。`.claude/rules/traceability.repo.md`）。
>
> **［同日・2 回目の拡大］`docs/adr/IADR-0114*` と `deploy/docker-compose.yml` も対象へ加えた。**
> 規則 10 の引き直しで、**`IADR-0114` が「現在の割当モデルは…`claude-fable-5`（`analysis`）」と現在形で
> 誤った記述を持つ**ことが分かったためである。**#850 は「計画 ADR に実装が追随していない」を直す作業であり、
> その過程で live な ADR に新しい誤りを残すのでは本末転倒になる。** 並行レーン 3 本がいずれも
> 両者を触っていないことが確認され、衝突しない。

## 3. 母集合の引き方と結果（`.claude/rules/traceability.repo.md` 規則 2・6・9・10）

**拡張子で絞らず、パス除外（`:!planning` `:!src/ai-stock-trading` `:!*/bin/*`）だけで追跡下の全ファイルを走査した。**
軸は 1 本で終わらせず、**誤りの側の文字列**で 4 本引いた。

| # | 軸（コマンド） | 目的 |
| --- | --- | --- |
| 1 | `git grep -c 'claude-fable-5' -- ':!planning' ':!src/ai-stock-trading' ':!*/bin/*'` | 設定値そのもの |
| 2 | `git grep -ci 'fable' -- （同上）` | 表記ゆれ・散文（`Fable 5` / `fable-5`） |
| 3 | `git grep -c 'NonZdr' -- （同上）` | 除外機構の側から |
| 4 | `git grep -n 'analysis' -- 'src' （同上）` | 用途キーの側から（`analysis` を送る呼び出し側の洗い出し） |

### 3.1 軸 1 の結果（`src/` 配下、`bin/` 除く）— 計 27 箇所 / 6 ファイル

| ファイル | 件数 | 判定 |
| --- | --- | --- |
| `src/.../LlmGateway.Api/appsettings.json` | 3 | **実効的な設定値。除去する**（`PurposeModels.analysis` / `Models` / `NonZdrModels`） |
| `src/.../LlmGateway.Api/Composable/Adapters/ClaudeProvider.cs` | 1 | コメント。**是正する**（記述が誤りになる） |
| `src/.../LlmGateway.Api/Foundation/Routing/EgressMatrix.cs` | 1 | コメント（除外機構の例示）。**残す**（§3.2） |
| `src/.../tests/LlmGateway.Api.Tests/CompletionRoutingEndpointTests.cs` | 5 | 期待値 1・否定表明 2・コメント 2。**期待値を是正する** |
| `src/.../tests/LlmGateway.Api.Tests/LlmRouterTests.cs` | 16 | **合成 config。残す**（#850 が明示指定。§3.2） |
| `src/.../tests/LlmGateway.Api.Tests/ClaudeProviderThinkingTests.cs` | 1 | **コメントであり実効値ではない**。是正する（§3.2） |

### 3.2 個別の判定と根拠

- **`LlmRouterTests.cs`（16 件）**: `Claude()` / `fableOnly` / `fableOnlyLead` の**合成 config** と、
  `Opts()` の合成 `PurposeModels` である。本番設定（`appsettings.json`）ではない。#850 は
  「ZDR 除外機構の単体カバレッジを失わないため残す」と明示指定している。**1 件も削らない。**
  ただし本番設定と乖離したことが読み手に伝わるよう、`Claude()` と `Opts()` へ日付つきの注記だけを足す。
- **`ClaudeProviderThinkingTests.cs`（1 件）**: **実効的な設定値ではない。**
  当該行はクラス冒頭の背景コメント（`analysis = claude-fable-5`）であり、テスト本体が
  `CompletionRequest` へ渡すモデル文字列は `"claude-sonnet-5"` と `null` だけである
  （`grep -n 'claude-' ClaudeProviderThinkingTests.cs` で全数確認した）。`ClaudeProvider` は
  ルーティングを行わないため、この値がテストの挙動を左右する経路は無い。
  よって**削除も期待値変更も不要**だが、**規則 10**（是正後に新たに誤りになる自分の記述を引き直す）により
  **記述としては誤りになる**ため、用途→モデルの対応を現行値へ書き改める。**黙って消さず、黙って残さない。**
- **`EgressMatrix.cs`（1 件）**: 「ZDR 非対応モデル（例: `claude-fable-5`）を候補から除外する」という
  **機構の説明の例示**である。機構は `NonZdrModels` に載る任意のモデルを対象とし、`claude-fable-5` は
  ZDR 非対応であるという契約事実（[[IADR-0113]] 決定 2）が変わったわけではない。よって**例示は誤りにならない**。
  ただし「本番設定では `NonZdrModels` が空である」ことは読み手に必要な事実なので、日付つきで補足する。
- **`appsettings.json` の `NonZdrModels`**: `claude-fable-5` を `Models` から外した以上、`NonZdrModels` に
  残す対象が無い。**空配列にする**（`LlmRouter.EligibleModels` は `Count == 0` を素通し経路として扱うため、
  空でも機構は壊れない）。キー自体は残す —— 削ると「機構ごと消えた」と読めるため。

### 3.3 `src/` の外（軸 2・3 の差分）と除外理由

| 箇所 | 内容 | 扱い |
| --- | --- | --- |
| `docs/adr/IADR-0022*` | `analysis` → `fable-5`・`NonZdrModels` 導入の決定 | **本 PR で日付つき追記**（旧条文は消さない） |
| `docs/adr/IADR-0112*` | 決定 2「`Models` / `NonZdrModels` は変更しない」 | **本 PR で日付つき追記** |
| `docs/adr/IADR-0113*` | 決定 2「`claude-fable-5` は `Models` に残す」／決定 4「`analysis` は意図的な例外」 | **本 PR で日付つき追記**（両決定の前提が覆った旨。`status` は `Accepted` 据え置き。理由は §7） |
| `docs/functional/FR-11_llm-egress-routing.md`（8 件） | 既定設定の割当表・受け入れ基準に `analysis→fable-5` | **本 PR で追随**。`status: in-progress` の **live な機能仕様書**であり凍結の射程外（§3.5） |
| `docs/tests/FR-11_llm-egress-routing.md`（4 件） | T-02 / T-11 / T-13 / T-23 の記述 | **本 PR で追随**。`status: completed` の **live なテスト仕様書**であり凍結の射程外（§3.5） |
| `docs/adr/README.md`（4 件） | ADR 索引行 | **領域外**（並行レーンと衝突しうるため触らない指示） |
| `docs/adr/IADR-0114*`（2 件） | §コンテキスト の「**現在の割当モデル**」列挙に `claude-fable-5`（`analysis`） | **本 PR で日付つき追記**（主題は無傷。§3.6） |
| `deploy/docker-compose.yml`（1 件） | 「最難関 fable-5」のコメント | **本 PR で現行値へ是正**（コード内コメントなので起点 ID `#850` を残す） |
| `src/.../AnthropicContentBlockSanitizer.cs`・`Program.cs`（各 1 件） | 「割当モデル（Opus 5 / Sonnet 5 / **Fable 5**）」 | **本 PR で是正**。**初回走査で挙げそこねた**（§3.6） |
| `.github/workflows/claude-*.yml`（各 1 件） | `@claude fable` と書かれたときのレビュー用モデル選択 | **無関係**。基盤サービスの用途別ルーティングではなく、CI の補助 AI の選択である。ADR-0038 の射程外 |
| `scripts/scripts.repo.test.js`（1 件） | Runbook に値を複写していないことの**否定表明**（`doesNotMatch`） | **無関係**。値が変わっても成立する |
| `CHANGELOG.md`（1 件） | 過去リリースの履歴 | **自動生成物。手で書き足さない** |
| `feedback/` / 確定済み `docs/specs/`（過去分） | 当時の point-in-time 記録 | **凍結の射程**（`traceability.repo.md`）。書き換えない |

### 3.4 規則 10 —— 是正後の語で引き直す

是正後（`analysis` = `claude-opus-5` / `NonZdrModels` が空）で新たに誤りになる自分の記述を、
`claude-opus-5` / `NonZdr` / `analysis` の 3 語で引き直した結果が §3.2 の
`ClaudeProviderThinkingTests.cs`・`EgressMatrix.cs`・`CompletionRoutingEndpointTests.cs` の
コメント群である（いずれも本 PR で是正する）。`LlmRoutingOptions.cs` の
「`NonZdrModels` に載るモデルを割り当てた用途は…」という警句は**機構の説明であり誤りにならない**ため触らない。

**［2026-08-18 追記 / #850］対象範囲を広げた後に、もう一度引き直した**（§2 の追記）。
**自分が本 PR で書いた記述のうち、範囲拡大によって偽になったもの**は次のとおりで、すべて是正した。

| 偽になった記述 | 置き場所 | 是正 |
| --- | --- | --- |
| 「`IADR-0113` 決定 2・決定 4 への追随は本件の射程外として別途扱う」 | `IADR-0112` の 2026-08-18 追記 | 「本 PR で `IADR-0113` にも同日追記を入れた」へ書き改め |
| 「本ガードの射程を `report-*` から全用途へ広げるかは…#850 では動かさない（追随は別 issue）」 | `CompletionRoutingEndpointTests.cs` のガード直前コメント | 射程を広げた旨と理由へ書き改め（テスト名も改名） |
| 「`docs/functional/` / `docs/tests/` / `IADR-0113` は領域外」 | 本仕様書 §2・§3.3・§4.2・§6・§7 | 本追記群で書き改め |

引き直しに使ったコマンドは §8 に生のまま残す（**記憶で挙げず、誤りの側の文字列で走査してから挙げた**）。

### 3.5 `docs/functional/` `docs/tests/` が凍結の射程に当たるかの判定

**当たらない。書き換えてよい。** 根拠は 3 つある。

1. **凍結の射程は記録種ごとに定まっており、対象は `docs/specs/`（確定済み）・`feedback/`・`docs/superpowers/` である**
   （[[IADR-0166]] 決定 2 の 2026-08-17 追記 / `.claude/rules/traceability.repo.md`）。
   `functional-spec` / `test-spec` は**その仕様書が記述する実装の現状**を述べる live な仕様書であり、
   「当時こう判断した」という point-in-time の記録ではない。
2. **`status` は凍結フラグではない。** `docs/README.md` 運用ルール 6 が明示するとおり
   `status` は「その仕様書が記述する**実装の状態**」であり、`completed` は「実装・テストが揃った」の意味である。
   **書き換え禁止を表す値ではない**（禁止を表すのは記録種のほうである）。
3. **直前例がある。** 同型の改定（[[IADR-0113]]・月報の割当変更）を入れた `404b1c3e`
   `fix(FR-11,IADR-0113): 月報の割当モデルを ZDR 対応の claude-opus-5 へ改定する (#429)` は、
   **両ファイルを同じ PR の中で直接書き換えている**（`git log -- docs/functional/FR-11_llm-egress-routing.md`
   / `docs/tests/FR-11_llm-egress-routing.md` で確認。`git rev-parse --is-shallow-repository` = `false` を
   先に確かめてから出典に用いた）。

したがって**新しい作業仕様書へ訂正を逃がす必要はなく**、両ファイルを直接更新し `updated:` を前進させた。


### 3.6 2 回目の規則 10 —— 走査の取りこぼしを 1 件認める

**［2026-08-18 追記 / #850］** `IADR-0114` と `deploy/` を対象へ加えたあと、**是正後の語で 3 度目の引き直し**を行った。
そこで**初回走査の取りこぼし 2 件**が出た。**隠さず記録する。**

| 取りこぼし | なぜ落ちたか |
| --- | --- |
| `src/.../AnthropicContentBlockSanitizer.cs:12` と `Program.cs:35` の「割当モデル（Opus 5 / Sonnet 5 / **Fable 5**）」 | **軸 2（`git grep -ci 'fable'`）はこの 2 件を検出していた**（初回報告の「fable（非 claude-fable-5）in src/deploy」に出ている）。にもかかわらず **§3.1 の表は軸 1（`claude-fable-5` 完全一致）の 6 ファイルだけで作られており、軸 2 の差分を表へ落とさなかった**。**軸を複数引いても、結果を 1 つの表へ合流させなければ意味が無い。** |

**教訓（次に同型を起こさないため）**: 複数軸で引いたら、**軸ごとの差分を明示的に合流させた 1 枚の表**を作る。
「軸 2 で見た」ことと「軸 2 の結果を判定した」ことは別である。

**3 度目の走査コマンドと結果**（表記ゆれを拾うため `claude-` 接頭辞に依存しない形で引いた）:

```
$ git grep -nE 'Fable 5|Fable-5|fable-5' -- 'src' 'deploy' ':!*/bin/*' ':!src/ai-stock-trading' \
    | grep -v 'claude-fable-5'
```

→ 是正対象 3 件（`deploy/docker-compose.yml:432` / `AnthropicContentBlockSanitizer.cs:12` / `Program.cs:35`）。
残りは `LlmRouterTests` の合成 config 文脈と、`CompletionRoutingEndpointTests` の履歴・注記であり誤りにならない。

**`claude-haiku-4-5` は書き足さない。** 現行の割当モデル集合は `claude-opus-5` / `claude-sonnet-5` /
`claude-haiku-4-5` だが、上記コメントはもともと haiku を挙げていない。**haiku-4-5 の thinking 既定の有無を
確かめた記録が本リポに無く、確かめずに書き足すと新しい未検証記述を作ることになる**（同じ理由で
`IADR-0114` の追記にもその旨を明記した）。**消す（Fable 5）だけにとどめる。**

## 4. 設計（変更内容）

### 4.1 `appsettings.json`

```
Llm:Routing:PurposeModels.analysis          : "claude-fable-5" → "claude-opus-5"
Llm:Routing:Endpoints[claude-managed].Models: "claude-fable-5" を除去（5 モデルへ）
Llm:Routing:Endpoints[claude-managed].NonZdrModels: ["claude-fable-5"] → []
```

`DefaultModel`（`claude-opus-5`）・他用途の割当・エンドポイント構成は変更しない。

### 4.2 テスト

- `CompletionRoutingEndpointTests.PostComplete_WithoutExplicitModel_SelectsPurposeModel`:
  `[InlineData("analysis", "claude-fable-5")]` → `"claude-opus-5"`。
- `PostComplete_ConfidentialAnalysis_FallsBackToZdrModel`: **改名する。**
  改定後の `analysis` は ZDR 対応モデルであり、この経路で ZDR 除外は**もう発火しない**。
  「フォールバックする」という名前のまま残すと、通っている経路と名前が食い違い、
  **空振りしているのに緑**という最も悪い状態になる。`PostComplete_ConfidentialAnalysis_ResolvesZdrModel`
  へ改め、「機密区分を上げても割当が変わらない」ことの回帰として意味を持たせる。
- `ReportPurposeModels_AreNotListedAsNonZdr` → **`PurposeModels_AreNotListedAsNonZdr` へ改名し、
  射程を `report-*` から全 `PurposeModels` へ広げる。**
  射程を `report-*` に絞っていたのは [[IADR-0113]] 決定 4 の「`analysis` は ZDR 非要件区分限定の
  意図的な例外なので対象に含めない」という前提による。**計画 `ADR-0038` 決定 2 でその例外が消滅した以上、
  絞る理由も消えた** —— 絞ったままだと `report-*` 以外の用途に非 ZDR モデルを割り当てる再発を捕まえられない。
  射程が覆った旨は [[IADR-0113]] §決定 の同日追記に記録した（**条文と実装を食い違わせたまま広げない**）。
  **広げたことが効いていることは変異試験で実測する**（§5.2）。
- `LlmRouterTests.cs`: **合成 config は 1 件も削らない**（§3.2）。注記のみ足す。

### 4.3 実装 ADR

[[IADR-0022]] と [[IADR-0112]] へ `［2026-08-18 追記 / #850］` の追記ブロックを置き、`updated:` を前進させる。
**旧条文は消さない**（`traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」）。

## 5. 受け入れ基準

- [x] `src/` 配下（`bin/` を除く）に `claude-fable-5` の**実効的な設定値**が 1 件も無い
      （合成 config・コメントは対象外。判定根拠は §3.2）。
- [x] `dotnet test src/platform/backend/backend.slnx` が緑。
- [x] **ZDR 除外機構のテストが空振りになっていない** —— 合成 config で発火し続けることを
      **実測で示す**（§5.1）。
- [x] `dotnet format src/platform/backend/backend.slnx --verify-no-changes` が通る。
- [x] 文書検査器（`check-doc-links` / `check-doc-status-vocabulary` / `check-doc-type-vocabulary` /
      `check-cross-repo-refs` / `check-plan-id-qualification` / `check-adr-numbering` /
      `check-reading-budget` / `check-kit-sync` / `scripts.test.js`）が通る。

### 5.1 受け入れ基準 3 の示し方（宣言ではなく実測）

`LlmRouterTests` の ZDR 除外系テスト（`Route_Confidential_SelectsProtectedExternalTierB` /
`Route_Restricted_Analysis_ExcludesNonZdrFable5` / `Route_Confidential_IgnoresRequestedNonZdrModel` /
`Route_Confidential_WhenAllModelsNonZdr_IsDenied` /
`Route_Confidential_FallsBackToNextCandidateWhenLeadHasNoZdrModel`）が
**実際に除外の分岐を通っている**ことを、**合成 config から `NonZdrModels` を一時的に外すと落ちる**ことで確かめる。
確認後は必ず元へ戻し、`git diff` が空であることで復元を確認する（**この変異はコミットしない**）。

**実測（2026-08-18）。合成 config は 2 系統あるので 2 回に分けて変異させた。**

| 変異 | 対象 | 結果（`dotnet test --filter FullyQualifiedName~LlmRouterTests`） |
| --- | --- | --- |
| 1 | `Claude()` の `NonZdrModels` を `[]` にする | **Failed: 3 / Passed: 37** —— `Route_Confidential_SelectsProtectedExternalTierB`（`Model` が index 7 で相違）／`Route_Restricted_Analysis_ExcludesNonZdrFable5`（同）／`Route_Confidential_IgnoresRequestedNonZdrModel`（`Expected decision.Model not to be "claude-fable-5"`） |
| 2 | `fableOnly` / `fableOnlyLead` の `NonZdrModels` を `[]` にする | **Failed: 2 / Passed: 38** —— `Route_Confidential_FallsBackToNextCandidateWhenLeadHasNoZdrModel`（`EndpointName` 相違）／`Route_Confidential_WhenAllModelsNonZdr_IsDenied`（`Expected decision.Allowed to be False, but found True`） |

**5 本すべてが除外の分岐に依存して緑になっている**（空振りしていない）。復元は
`sha256sum -c`（変異前ハッシュ `2f6f030a…`）が `OK` を返すことと、
`git diff` の削除行がコメント 1 行だけ（合成 config の 16 件は 1 件も削られていない）ことで確認した。
**変異はコミットしていない。**

### 5.2 ガードの射程を広げたことの実測（§4.2）

**広げたなら、広げたぶんが「壊すと落ちる」ことを示す。** 本番設定の `NonZdrModels` は空なので、
広げた `PurposeModels_AreNotListedAsNonZdr` は素のままでは自明に通る。そこで
**`appsettings.json` の `NonZdrModels` へ `claude-haiku-4-5` を一時的に入れる**変異を行った。
**`claude-haiku-4-5` を使う用途は `diagram-coding` だけ**であり（`report-*` は `claude-opus-5` /
`claude-sonnet-5`）、**旧射程（`report-*` のみ）では絶対に捕まらない**値である。

| 観点 | 結果 |
| --- | --- |
| 広げたガード単体 | **Failed: 1 / Passed: 0** —— `Expected claude.NonZdrModels {"claude-haiku-4-5"} to not contain "claude-haiku-4-5" because 用途 diagram-coding の割当モデル…` |
| LlmGateway 全 157 本 | **Failed: 1 / Passed: 156** —— **落ちたのは広げたガード 1 本だけ**。`report-*` 系のテスト（T-22 / T-23）は**すべて緑のまま**であり、**旧射程がこの再発に対して盲目であったこと**をそのまま示している |

復元は `sha256sum -c`（変異前 `1ccc19fc…`）が `OK` を返すことと、
`git diff -- …/appsettings.json` が空であることで確認した。**変異はコミットしていない。**

## 6. 計画書との差異・追随先

- **差異: なし。** 計画 ADR-0038 決定 1・2 に忠実である。
- **決定 3・4・6（`analysis` のフォールバック順序 `claude-opus-5` → `claude-sonnet-5`、
  429 は再試行でありフォールバックではない、フォールバック発火の可観測化）は本 PR では実装しない。**
  現行 `LlmRouter` は「用途別モデル → `DefaultModel` → 適格モデルの先頭」という**解決順序**を持つだけで、
  **HTTP 400 系での実行時フォールバック機構そのものを持たない**。決定 3・4・6 は機構の新設を伴い、
  #850 の「やること」にも受け入れ基準にも含まれていない。**別 issue を要する**（§7）。
- **本 PR で追随させた文書**（§2 の追記で範囲を広げた分）: `docs/adr/IADR-0113*`（決定 2・決定 4 の前提が覆った旨）、
  `docs/functional/FR-11_llm-egress-routing.md`（8 件）、`docs/tests/FR-11_llm-egress-routing.md`（4 件）。
- **2 回目の拡大で追随させた文書**: `docs/adr/IADR-0114*`、`deploy/docker-compose.yml`、
  および実装コメント 2 件（`AnthropicContentBlockSanitizer.cs` / `Program.cs`）。
- **なお領域外に留めた文書**: `docs/adr/README.md` のみ（索引行が並行レーンと衝突しうるため）。

## 7. 未決事項

- **［2026-08-18 追記 / #850・解決済み］[[IADR-0113]] へ追記すべきか**: **すべきであり、本 PR で追記した。**
  同 IADR の決定 2 は「`claude-fable-5` は `Models` に**残す**」、決定 4 は
  「`analysis` は ZDR 非要件区分限定の意図的な例外」と述べており、**どちらも計画 ADR-0038 決定 2 が覆した**。
  同 IADR は「月報のみ」を改めたものだが、本件で誤りになるのは月報の話ではなく
  **`Models` に残すという判断そのもの**である。当初は領域外として保留したが、
  並行レーンが触っていないことが確認され範囲が広がったため、**§決定 の冒頭へ日付つき追記を入れた**。
  - **`status` は `Accepted` に据え置いた。** 覆ったのは決定 2・4 であって全体ではなく、
    **決定 1（月報 = `claude-opus-5`）・決定 3（機密区分を下げない）は現行の実装そのもの**である。
    `Superseded` にすると「月報の割当を決めた記録」ごと無効に見え、後続が現行値を読み違える。
    **本文は 1 文字も書き換えていない**（旧条文は原文のまま、追記ブロックで訂正した）。
- 決定 3・4・6（フォールバック機構）の実装 issue。**新規 IADR の採番が要る見込みであり、
  本 PR では起こさない**（採番衝突の回避）。
- `analysis` と `default` が同一モデル（`claude-opus-5`）になったため、
  `PostComplete_WithoutExplicitModel_SelectsPurposeModel` の `analysis` ケースは
  「用途別割当が発火したこと」と「`DefaultModel` へ落ちたこと」を**区別できない**。
  計画 ADR-0038 §結果 が「用途を分けている意味が薄れる」として受け入れたトレードオフそのものであり、
  区別は `PurposeModels_AreAllRegisteredInClaudeEndpointModels`（T-19）と
  `LlmRouterTests` の合成 config 側が引き続き担う。
