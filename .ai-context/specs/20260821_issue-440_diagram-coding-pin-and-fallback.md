---
title: 作業仕様書 — diagram-coding のピンを claude-sonnet-5 へ移し、フォールバック鎖を計画どおりに登録する
type: spec
status: done
related_ids:
  - FR-11
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - "ADR-0038（用途別モデル割当の改定）"
  - "06_technical/04_ai-rag-stack.md §用途別フォールバック順序（fixed）"
related_adrs:
  - IADR-0102
  - IADR-0112
  - IADR-0225
issue: "#440"
---

# 作業仕様書: `diagram-coding` のピンとフォールバック鎖を計画へ追随させる

## 起点となる計画書（トレーサビリティ）

- 機能要求: `FR-11`（LLM ゲートウェイ）
- 関連計画 ADR: `ADR-0038`（用途別モデル割当）
- 計画書: `06_technical/04_ai-rag-stack.md`（**fixed**）§用途別フォールバック順序・§変更履歴

## 着手前の再検証（#440 本文は大きく古い）

`#440` は「用途別モデル割当の改定（analysis=Opus 5・Fable 5 全面不使用）とフォールバック実装」を
求めているが、**大半は既に着地している**（`#850` / `#863`）。2026-08-21 の実測:

| 項目 | 実測 | 判定 |
| --- | --- | --- |
| `analysis` = `claude-opus-5` | `appsettings.json` の `PurposeModels.analysis` が `claude-opus-5` | ✅ 済 |
| `claude-fable-5` の全面不使用 | `Models` 配列・割当の双方に出現なし | ✅ 済 |
| `analysis` のフォールバック | `PurposeFallbackModels.analysis = [claude-sonnet-5]` | ✅ 済 |
| **`diagram-coding` のピン** | **`claude-haiku-4-5`**。計画は **`claude-sonnet-5`** | ❌ **未追随** |
| **`diagram-coding` のフォールバック** | **鎖なし**。計画は `[claude-haiku-4-5]` | ❌ **未追随** |
| `default` / `rag-answer` のフォールバック | 鎖なし | ⚠ **計画内で不整合（下記）** |

引いたコマンド: `grep -rn "diagram-coding" src/platform/backend/Services/LlmGateway/`（bin/ を除く実体 3 件）。

## スコープ

`diagram-coding` の**ピン**と**フォールバック鎖**を計画へ追随させる。

計画 `04_ai-rag-stack.md`（fixed）は次を確定させている。

- §変更履歴 2026-08-02: 「用途 `diagram-coding` のピンを `claude-haiku-4-5` → `claude-sonnet-5` へ変更
  （**単価 3 倍**）。`claude-haiku-4-5` をフォールバック先として利用許可集合に残す」
  （質問票 第4回 Q12 =(あ)・planning#83）
- §用途別フォールバック順序の表: `diagram-coding` の第 1 = `claude-sonnet-5` / 第 2 = `claude-haiku-4-5`

`INDEX.md` 決定 6 も同じ内容を持つ。**実装だけが 2026-08-02 の裁定に追随していない。**

### スコープ外 — `default` / `rag-answer` のフォールバック（計画内の不整合のため実装しない）

**計画の 2 文書が食い違っている。**

| 文書 | 状態 | 記述 |
| --- | --- | --- |
| `06_technical/04_ai-rag-stack.md` | **fixed** | §用途別フォールバック順序の表と §変更履歴が「**2026-08-07 確定**（利用者裁定）。`default` → `claude-sonnet-5`、`rag-answer` → `claude-haiku-4-5`」と書く |
| `07_adr/ADR-0038` | **Accepted** | §未決事項の表が「`default` / `rag-answer` の**フォールバック第 2 候補** … **未確定**。本 ADR の対象外である」と書く。フォローアップ 5 にも「確定」が残る |

`ADR-0038` は 2026-08-17 に `Accepted` へ移っており、**2026-08-07 の裁定より後に「未確定」と書いている**。
実装側のコメント（`LlmRoutingOptions.cs` 制約 3）も `ADR-0038` を典拠に「根拠なく足さない」としている。

**どちらが正かは実装側で決めない**（CLAUDE.md 手順 2「曖昧な場合は実装を止め、人間に確認する」）。
`/plan-feedback` で planning へ環流し、裁定を待つ。

## 受け入れ基準

1. `PurposeModels["diagram-coding"]` が `claude-sonnet-5` である
2. `PurposeFallbackModels["diagram-coding"]` が `["claude-haiku-4-5"]` である
3. 鎖の要素 `claude-haiku-4-5` が `claude-managed` の `Models`（利用許可集合）に登録されている
   （`ADR-0038` 決定 5。未登録だと `LlmRouter` が warn を出して鎖から落とす）
4. 割当スナップショットテストが新しい値で通る
5. `dotnet test src/platform/backend/backend.slnx` が Failed=0
6. **変異試験**: ピンを `claude-haiku-4-5` へ戻すとスナップショットテストが fail する
7. planning へ不整合の環流 issue を起票した → **planning#426**

---

## ［2026-08-21 追記 / #440・planning#426］スコープ外としていた 2 用途の鎖を、裁定を受けて実装する

上の「スコープ外」節は **planning#426 の裁定によって解消した**。同 issue は 2026-08-21T05:17Z に
**裁定 (a)** でクローズされている。

### 裁定の内容（要旨）

- **`04_ai-rag-stack.md`（fixed）が正**である。`ADR-0038` §未決事項の「未確定」は**追随漏れ**であり、
  2026-08-07 の利用者裁定より後に書かれた誤りであった。計画側は 3 箇所（§未決事項の表・
  §フォローアップ 5・関連記述）を打ち消し線＋日付つき追記で是正した（planning#427）。
- **`PurposeFallbackModels` へ `default` → `claude-sonnet-5`、`rag-answer` → `claude-haiku-4-5` を登録してよい。**
- `LlmRoutingOptions.cs` 制約 3 のコメント（「…未確定である。根拠なく足さない」）は
  **根拠を失ったため書き換えること。**
- 両モデルが `claude-managed` の `Models` に登録済みであることを確認すること。

### 追加スコープ

`default` / `rag-answer` の鎖を登録し、**登録によって偽になる自分の記述**を追随させる。

### 母集合（規則 9・10 に従い、誤りの側の文字列で引いた）

引いたコマンド（追跡下のファイルのみ。`src/ai-stock-trading` は別プロジェクトのため除外）:

```
git grep -n "第 2 候補\|ADR-0038 §未決事項\|鎖を持つのは\|鎖を持たない" -- . ':!src/ai-stock-trading'
```

軸を変えて `rag-answer` 単独でも引き直した（規則 5）。**是正対象は 7 件**である。

| # | 箇所 | 誤りの内容 | 対応 |
| --- | --- | --- | --- |
| 1 | `appsettings.json` | 鎖が 2 用途しかない | 4 用途へ拡張 |
| 2 | `LlmRoutingOptions.cs` 制約 3 | 「未確定。根拠なく足さない」 | 日付つき追記で是正（裁定が名指し） |
| 3 | `CompletionEndpoints.cs` ストリーム経路の注記 | 「鎖を持つのは analysis だけ」「rag-answer は第 2 候補が未確定」 | **どちらも現状と合わない。** 是正しつつ決定 4 の維持理由を書き直す |
| 4 | `LlmRouterTests.cs` の合成 config | 「本番設定と同じ値」と称しつつ `diagram-coding` を写し忘れ、`default` / `rag-answer` も無い | 本番と同じ 4 用途へ揃える |
| 5 | `docs/functional/FR-11` §既定値・§未決事項 | 「`analysis` のみ」「第 2 候補は未確定」 | 4 用途へ是正し、未決事項は解決として畳む |
| 6 | `docs/operations/llm-model-pin-runbook.md` | 「鎖を持つのは `analysis` だけ」 | **`diagram-coding` を数え落としていた**（裁定以前から誤り）。4 用途へ是正 |
| 7 | `.ai-context/adr/IADR-0225` | フォローアップ 2 が未解決のまま／決定 4 の前提が崩れた | **本文は書き換えず**、日付つき追記ブロックで解決と前提の崩れを記録 |

**除外したもの（理由つき）:**

- `docs/tests/FR-11` T-25 行 —— 誤りではないが**新しい受け入れ基準を持たない**ため、⑤の言い換えと⑦の追加を行った（是正ではなく拡充）。
- `.ai-context/specs/` の他の凍結記録・`CHANGELOG.md`・`bin/` `obj/` の生成物 —— 凍結記録は書き換えない／生成物は追随不要。
- `LlmCompletionMetrics.cs:39` / `LlmRouter.cs:62` / `LlmRoutingOptions.cs:25` の「第 2 候補以降」 —— **機構の説明**であり、鎖の中身に依存しない。誤りではない。
- `AST` 側（`src/ai-stock-trading`）—— 別プロジェクトの計画・実装であり本裁定の射程外。

### 受け入れ基準（追加分）

8. `PurposeFallbackModels` が `analysis` / `diagram-coding` / `default` / `rag-answer` の 4 用途を持つ
9. 鎖の全要素が `claude-managed` の `Models` に登録済みである
   （既存ガード `PurposeModelsAndFallbacks_AreAllRegisteredInClaudeEndpointModels` が固定する）
10. `rag-answer` が HTTP 400 で `claude-haiku-4-5` へ落ちることを HTTP 経路のテストが固定する
11. **「鎖を持たない用途は落ちない」という分岐が、鎖を持たない用途を題材に固定され続ける**
    （題材を `default` / `rag-answer` から `report-weekly` へ移す）
12. `dotnet test src/platform/backend/backend.slnx` が Failed=0
13. **変異試験**（2 本とも「変異が実際に入ったこと」を確認してから判定する）
    - `rag-answer` の鎖を外す → 新テストが fail する
    - 鎖を持たないはずの `report-weekly` に鎖を足す → 「落ちない」テストが fail する

### 実測（2026-08-21）

```
$ dotnet test .../LlmGateway.Api.Tests
Passed!  - Failed: 0, Passed: 183, Total: 183     ← 182 → 183（T-25e2 の新設分）

=== 変異 1: rag-answer の鎖を外す ===
（appsettings.json から 1 行削除したことを grep で確認してから実行）
Failed!  - Failed: 1, Passed: 182
  → PostComplete_RagAnswer_When400_FallsBackToHaiku45 [FAIL]

=== 変異 2: report-weekly に鎖を足す ===
（追加行を grep -c で 1 件と確認してから実行）
Failed!  - Failed: 1, Passed: 182
  → PostComplete_ReportWeekly_When400_DoesNotFallBack [FAIL]
```

**いずれも変異を戻し、`git diff` で復旧を確認した。**

### 素通りした変異（開示）

- **合成 config（`LlmRouterTests.Claude()`）から `default` / `rag-answer` の鎖を外しても、
  `LlmRouterTests` は緑のままである。** 合成 config を見るテストは本番設定を見ておらず、
  この乖離自体を検出する仕組みは無い（実際、`diagram-coding` の鎖は**写し忘れたまま緑だった**）。
  本 PR では値を揃えたが、**揃い続けることを機械が保証していない**。
  「本番と合成の突合テスト」を足すかは、**同型の事故が 2 回起きたら**の規準に照らして
  今回は記録に留める（1 回目である）。
