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

- 対象: `src/platform/backend/Services/LlmGateway/**`、`docs/adr/IADR-0022*`、`docs/adr/IADR-0112*`、本仕様書。
- 対象外（理由つきで §3.3 に列挙）: `docs/functional/` / `docs/tests/` / `docs/adr/IADR-0113*` /
  `docs/adr/README.md` / `deploy/` / `scripts/` / `.github/workflows/`。

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
| `docs/adr/IADR-0113*` | 決定 2「`claude-fable-5` は `Models` に残す」／決定 4「`analysis` は意図的な例外」 | **要追随だが本 PR の領域外**（§6） |
| `docs/functional/FR-11_llm-egress-routing.md`（8 件） | 既定設定の割当表・受け入れ基準に `analysis→fable-5` | **領域外**（他レーンの領分）。§6 に追随先として明記 |
| `docs/tests/FR-11_llm-egress-routing.md`（4 件） | T-02 / T-11 / T-13 / T-23 の記述 | **領域外**。§6 |
| `docs/adr/README.md`（4 件） | ADR 索引行 | **領域外**（並行レーンと衝突しうるため触らない指示） |
| `deploy/docker-compose.yml`（1 件） | 「最難関 fable-5」のコメント | **領域外**。§6 |
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
- `ReportPurposeModels_AreNotListedAsNonZdr`: **射程（`report-*`）は変えない。**
  射程は [[IADR-0113]] 決定 4 が定めており、全用途へ広げるには同 IADR の改定が要る（本 PR の領域外・§6）。
  末尾の「`analysis` は意図的な例外」という注記だけが誤りになるので、日付つきで是正する。
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

## 6. 計画書との差異・追随先（本 PR の領域外）

- **差異: なし。** 計画 ADR-0038 決定 1・2 に忠実である。
- **決定 3・4・6（`analysis` のフォールバック順序 `claude-opus-5` → `claude-sonnet-5`、
  429 は再試行でありフォールバックではない、フォールバック発火の可観測化）は本 PR では実装しない。**
  現行 `LlmRouter` は「用途別モデル → `DefaultModel` → 適格モデルの先頭」という**解決順序**を持つだけで、
  **HTTP 400 系での実行時フォールバック機構そのものを持たない**。決定 3・4・6 は機構の新設を伴い、
  #850 の「やること」にも受け入れ基準にも含まれていない。**別 issue を要する**（§7）。
- 追随が必要だが領域外の文書: `docs/adr/IADR-0113*`（決定 2・決定 4）、
  `docs/functional/FR-11_llm-egress-routing.md`、`docs/tests/FR-11_llm-egress-routing.md`、
  `docs/adr/README.md`、`deploy/docker-compose.yml`。

## 7. 未決事項

- **[[IADR-0113]] へ追記すべきか**: **すべきである。** 同 IADR の決定 2 は
  「`claude-fable-5` は `Models` に**残す**」、決定 4 は「`analysis` は ZDR 非要件区分限定の意図的な例外」
  と述べており、**どちらも計画 ADR-0038 決定 2 が覆した**。同 IADR は「月報のみ」を改めたものだが、
  本件で誤りになるのは月報の話ではなく**`Models` に残すという判断そのもの**である。
  ただし `docs/adr/IADR-0113*` は**本 PR の領域外**（並行レーンとの衝突回避）であるため、
  **本 PR では触らず、親へ報告して追随 issue の起票を仰ぐ**。
- 決定 3・4・6（フォールバック機構）の実装 issue。**新規 IADR の採番が要る見込みであり、
  本 PR では起こさない**（採番衝突の回避）。
- `analysis` と `default` が同一モデル（`claude-opus-5`）になったため、
  `PostComplete_WithoutExplicitModel_SelectsPurposeModel` の `analysis` ケースは
  「用途別割当が発火したこと」と「`DefaultModel` へ落ちたこと」を**区別できない**。
  計画 ADR-0038 §結果 が「用途を分けている意味が薄れる」として受け入れたトレードオフそのものであり、
  区別は `PurposeModels_AreAllRegisteredInClaudeEndpointModels`（T-19）と
  `LlmRouterTests` の合成 config 側が引き続き担う。
