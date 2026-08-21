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
