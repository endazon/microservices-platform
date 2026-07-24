---
title: IADR-0100 既定モデルを Claude Opus 5 へ追従し、思考の既定有効化に伴い既定 max_tokens を 1024 → 4096 へ引き上げる
type: impl-adr
status: Accepted
related_ids:
  - FR-11
  - ADR-0010
  - ADR-0025
  - IADR-0022
author: claude
created: 2026-07-24
updated: 2026-07-24
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md (グローバル既定を Claude Opus 5 へ改定・Accepted)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (LLM ゲートウェイ設計・Accepted・本文凍結)"
  - "../../planning/projects/microservices-platform/06_technical/04_ai-rag-stack.md (用途別モデル表)"
---

# IADR-0100: 既定モデル Claude Opus 5 追従と既定 max_tokens の引き上げ

- 状態: Accepted
- 日付: 2026-07-24
- 決定者: claude（実装）

## 起点・関連

- 計画根拠: [ADR-0025](../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md)
  がグローバル既定を `claude-opus-4-8` → `claude-opus-5` に改定した。実装はこれに追従する。
- 既定 Opus・fable-5／Copilot 経路の設計は [[IADR-0022]]。本 IADR は**そのモデル版数のみ**を更新し、
  ルーティング設計・ティア判定・ZDR 除外ロジックは変更しない。
- 仕様書: `docs/specs/20260724_adr-0025_default-model-opus-5.md`。

## コンテキストと課題

モデル ID の差し替えだけでは足りない挙動差が 1 点ある。

| | Opus 4.8 | Opus 5 |
| --- | --- | --- |
| `thinking` 省略時 | 思考なし | **adaptive thinking が有効** |
| `max_tokens` の意味 | 実質「本文の上限」 | **思考トークン＋本文の合算上限** |
| 単価 / トークナイザ | $5 / $25 per MTok | **同額・同一トークナイザ** |
| ZDR | 対応 | 対応（30 日保持要件なし） |

現行の呼び出しは `MaxTokens = 1024`（`CompletionRequest` の既定値、および `RagOrchestrator` が
明示指定する値）である。Opus 5 ではこの 1024 を思考が消費するため、**本文が途中で切れる**。
これは実行時に例外にならず、短い/尻切れの回答として静かに縮退するため検知しにくい。

なお現行 `ClaudeProvider` は `thinking` / `temperature` / `top_p` / `top_k` / assistant prefill を
一切送信していないため、Opus 5 で 400 を返す破壊的パラメータは存在しない。差分は上表の 1 点に閉じる。

## 検討した選択肢

1. **既定 `max_tokens` を引き上げ、思考は有効のまま使う（採用）** — 思考分の余裕を確保する。
   計画の「既定は最新かつ最も高性能な Claude モデル」原則に最も素直に沿う。
2. `thinking: {type:"disabled"}` を明示送信して Opus 4.8 相当の挙動に固定する — `max_tokens` を
   据え置ける。ただし (a) 現行はいかなる `thinking` も送っておらず**新規パラメータの導入**になる、
   (b) Opus 5 では思考無効時にツール呼び出しが本文テキストへ漏れる／`<thinking>` タグが
   可視応答に混入する既知の失敗モードがある、(c) effort `xhigh`/`max` と併用できない制約が付く。
   モデルを新しくしながら思考だけ止めるのは改定の意図（品質向上）とも噛み合わない。
3. `max_tokens` を移行ガイドの一般既定（非ストリーミング ~16000）まで引き上げる — 安全側だが、
   本リポジトリは `05_observability-ops` でコスト最適化を明示しており、RAG 回答の想定長に対して過大。

## 決定

- 既定モデル文字列を `claude-opus-4-8` → **`claude-opus-5`** に更新する
  （`Llm:Model` / `Llm:Routing:PurposeModels.default` / claude エンドポイントの `DefaultModel`・`Models`、
  および `ClaudeProvider` / `RagOrchestrator` のフォールバック値）。
- 既定 `max_tokens` を **1024 → 4096** へ引き上げる
  （`CompletionRequest.MaxTokens` の既定値、および `RagOrchestrator` が渡す `CompletionApiRequest.MaxTokens`）。
- `thinking` パラメータは**送信しない**（選択肢 2 を採らない）。

## 理由

- 4096 は「従来どおりの本文長（〜1024 相当）＋ adaptive thinking の作業領域（〜3000）」を見込んだ値である。
  移行ガイドの一般既定 16000 まで引き上げず、`05_observability-ops` のコスト最適化方針との整合を優先した。
  これは**実測前の出発値**であり、出力トークンの実測後に再調整する（下記フォローアップ）。
- 単価・トークナイザが Opus 4.8 と同一のため、入力側のコスト試算ベースラインは据え置ける。
  増えるのは思考分の**出力**トークンのみであり、影響範囲が限定的で見積もりやすい。
- ZDR の位置付けが Opus 4.8 と同じであるため、`NonZdrModels`（`claude-fable-5` のみ）と
  ZDR 除外フォールバックのロジックは無変更で意味が保たれる。テスト T-13 の意図も維持される。

## 結果

- 良い影響: 既定層の品質が向上する。設定駆動のため基盤・契約・API 互換性への影響はない。
- 悪い影響 / トレードオフ:
  - 既定層の**出力トークンが増え、その分コストが増える**（思考分）。単価は据え置きだが総額は上振れし得る。
  - `max_tokens` 引き上げにより、異常系で 1 応答あたりの最大コストが 4 倍になり得る。
    使用量の可視化（Grafana）と月次上限アラートで検知する運用は従来どおり。
- フォローアップ:
  1. **出力トークンの実測と 4096 の再調整**。実測で思考が収まらない／過剰に余っている場合に見直す。
  2. **Opus 5 のレート制限枠の確認**。Opus 4.x 系とは別枠のため、既定層のトラフィック移行で
     429 が出ないことを確認する。
  3. **`stop_reason: "refusal"` のハンドリング検討**。Opus 5 はサイバー系の安全性分類器を持ち
     HTTP 200 + `refusal` を返し得る。現行は空応答へ縮退し例外にならないため即時の不具合には
     ならないが、監査ログ上「送信したが空応答」と区別できない。必要なら別 IADR で起票する。
  4. `rag-answer` に割り当てた Sonnet 5（[ADR-0022] 追従）も **thinking が既定有効**である。
     本 IADR の `max_tokens` 引き上げは同経路にも効くが、Sonnet 5 側の最適値は別途実測する。

## 関連

- Supersedes: なし（[[IADR-0022]] のモデル版数のみ更新。ルーティング設計は不変更）
- Superseded by: なし
- 関連要求 / UC: FR-11（LLM 送信可否の統制）
