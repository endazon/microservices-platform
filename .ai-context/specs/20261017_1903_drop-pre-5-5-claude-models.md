---
title: 作業仕様書 — LLM ゲートウェイの利用許可集合・単価表・effort の対応集合から旧世代の Claude モデル ID を外し、5.5 系の 3 モデルだけにする（#1903）
type: spec
status: done
related_ids: [FR-10, FR-11, ADR-0017, ADR-0044, ADR-0138, IADR-0102, IADR-0531]
author: claude
created: 2026-10-17
updated: 2026-10-17
issue: "#1903"
---

# 作業仕様書 — 旧世代の Claude モデル ID を外し、5.5 系の 3 モデルだけにする（#1903）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。基点は MSP `origin/develop` `f0c76c05`。
> 計画 ADR-0138（Accepted・2026-10-10）は隣接クローン `../project-planning`（読み取り専用）で読んだ。
> 本件で新しい IADR は起こさない。IADR-0531 決定 1 の「旧 ID を切り戻し用に残す」と配備順序を、日付つき追記で改める。

## 起点となる計画書・裁定（トレーサビリティ）

- 機能要求: **FR-11**（呼び出し先の切替。利用許可集合）・**FR-10**（費用の計測。単価表）。
- 計画 ADR: **ADR-0138** 決定 7（許可集合は `claude-opus-5-5`・`claude-sonnet-5-5`・`claude-haiku-5-5` の 3 つだけ。単価表に旧 ID の行を残さない）・
  決定 8（移行段を設けず、MSP と AST を同時に配備する）・決定 1（`claude-opus-4-8`・`claude-sonnet-4-6` も外す）・フォローアップ 1。
  **ADR-0044** 決定 3（単価表）・**ADR-0017**（取引判断）。
- 裁定: planning#783 のコメント（2026-10-10）の裁定 3・4。
- 実装 ADR: IADR-0531（決定 1 の改め）・IADR-0102（許可集合に無いモデルは既定へ落ちる経路）。

## 着手の条件

- ADR-0138 は着手可否の注記を置かない（未検証の事項を「成立する」と主張しない。決定 7・8 は利用者の裁定）。
- 決定 9 の表は、旧 ID を外すのは「5.5 系の配備と同時であり、先に外さない」とする。**5.5 系への切替（#1875 / IADR-0531）は develop にある。**
  AST 側の移行段の撤去（AST#1296 → AST#1310）と、AST の単価表からの `claude-fable-5` の行の撤去（AST#1380）は AST の develop にマージ済みである。
- オーナーの判断待ち・PoC の確認待ちで止まる前提は残っていない。配備は ADR-0138 決定 8 のとおり AST と同時に行う（PR 本文に書く）。

## 外す前の走査（配備中のモデルを外さないことの確認）

旧 ID の正規表現 `claude-(opus-5|sonnet-5|haiku-4-5|opus-4-8|sonnet-4-6|fable-5(-1)?)(?![-\w.])`（`-5-5` を誤って拾わない）で走査した。

| 対象 | 版 | 割当で旧 ID を使う箇所 | 走査の結果 |
| --- | --- | --- | --- |
| MSP `appsettings.json` の `Llm:Model`・`PurposeModels`（12 用途）・`PurposeFallbackModels`（9 用途）・`DefaultModel` | `f0c76c05` | **無い** | 5.5 系だけ。旧 ID は単価表（`:68-76`）と `Models`（`:123`）だけ |
| MSP `ClaudeProvider.cs` の直書き既定 | 同 | **無い** | `?? "claude-opus-5-5"` |
| MSP `deploy/`（compose・helm・local） | 同 | **無い** | `Llm__Model: claude-opus-5-5` だけ。`Models`・単価表・`PurposeModels` の上書きは無い。旧 ID はコメントだけ |
| MSP knowledge ユニットの呼び出し側（明示の `Model` を送る箇所） | 同 | **無い** | 旧 ID はコメントだけ |
| AST `LlmAssignments.All`（割当表） | AST `origin/develop` `98f28a22` | **無い** | `Sonnet55`・`Opus55`・`Haiku55` だけ。`claude-fable-5`・`claude-fable-5-1` は禁止モデルの定数（使わない側の宣言）として残る |
| AST の設定・helm（テスト以外） | 同 | **無い** | 旧 ID はコメントと、helm.yml の「fable-5 の行が描画されたら落とす」検査だけ |

したがって、旧 ID を外しても本番の呼び出しの割当は変わらない。明示の `Model` で旧 ID を送る呼び出し側も無い。

## 母集合（是正・追随の対象。規則 9・10）

上の正規表現で全文書を走査してから挙げた。

- **変える（現行値を書く live な箇所）**
  - `src/platform/backend/Services/LlmGateway/appsettings.json`: 単価表の旧 3 行・`Models` の旧 5 ID。
  - `ClaudeEffort.cs`: `CapableModels` の旧 3 ID と、切り戻しを前提にしたコメント。
  - 試験: `DeployedPriceTableTests`（旧 ID の単価を固定する 2 つ）・`ModelPricingOptionsValidatorTests`（配備中の表の写し）・
    `ClaudeProviderRequestShapeTests`（切り戻しの前提の試験）。
  - 文書: `docs/functional/FR-11_llm-egress-routing.md`（許可集合・effort の対応モデル・旧 ID を残す記述）・
    `docs/operations/llm-model-pin-runbook.md`（旧版を残す・配備の順序・切り戻し）・`docs/operations/operations.md`（effort の効く範囲・
    許可集合に残る旧モデル）・`docs/tests/FR-10_dashboard.md`（T-45・T-48）・`docs/tests/FR-11_llm-egress-routing.md`（追加の試験）。
  - `IADR-0531`: 日付つき追記（決定 1 の改め・配備順序）。
- **変えない（除外の理由）**
  - 過去の決定・経緯を書いたコメントと文書（「旧 `claude-opus-5` は 5.0 / 25.0 USD」「2026-10-10 に `claude-haiku-4-5` から切替」など）: 当時の記録であり、現行値を主張しない。
  - `LlmRouterTests`・`ModelPriceTableTests` の合成 config（`claude-fable-5`・`claude-sonnet-5`・`claude-opus-4-8` など）: 仕組みの試験で、実設定を読まない。
  - `.ai-context/specs/`・`CHANGELOG.md`: 凍結記録・生成物。
  - `ClaudeProviderThinkingTests` ほか、旧 ID がコメントだけの試験。

## 受け入れ基準と試験の写像

| # | 受け入れ基準（issue #1903） | 試験 |
| --- | --- | --- |
| 1 | 既定の構成で、利用許可集合と単価表に 5.5 系以外の Claude モデルが無い | `CompletionRoutingEndpointTests.ClaudeEndpointModels_AreExactlyThe55Series`・`DeployedPriceTableTests.実設定の単価表は55系の3モデルだけを持つ`・`ClaudeEffort` の対応集合が許可集合と一致する試験 |
| 2 | 旧 ID を明示の `Model` で要求すると、許可集合に無いモデルとして扱われる（IADR-0102 の既定へ落ちる経路は現行どおり） | `CompletionRoutingEndpointTests.PostComplete_ExplicitPre55Model_IsNotHonoredAndFallsBackToPurposeModel`（用途の割当へ落ちる。用途の割当が無ければ `DefaultModel`）・`DeployedPriceTableTests.実設定は旧世代のモデルの単価を持たない` |
| 3 | 5.5 系の 3 モデルの単価は現行どおり（haiku-5-5 の 2 段を含む） | 既存の `DeployedPriceTableTests.実設定の55系の単価で換算される`（変更なし） |
| 4 | 否定形: 割当・フォールバックの候補に 5.5 系以外が現れない | 既存の `PurposeModelsAndFallbacks_AreAllRegisteredInClaudeEndpointModels`（割当と鎖が許可集合に含まれる）＋ 1 の「許可集合は 5.5 系の 3 つだけ」で閉じる |

## 変異の確認（予定）

- `Models` に `claude-opus-5` を戻す → 1 が赤。
- 単価表に `claude-haiku-4-5` の行を戻す → 1・2（単価）が赤。
- `CapableModels` に `claude-opus-4-8` を戻す → effort の対応集合の試験が赤。

## 対象外

- rerank の effort を low にしたことの遅延の実測（IADR-0531 の残作業）。
- 合成 config の旧 ID の整理（仕組みの試験であり、実設定を主張しない）。
