---
title: IADR-0106 定型 RAG 回答（rag-answer）を Claude Sonnet 5 へ追随し、許可モデル集合へ登録する
type: impl-adr
status: Accepted
related_ids:
  - FR-04
  - FR-11
  - UC-01
  - ADR-0010
  - ADR-0022
  - ADR-0025
  - IADR-0022
  - IADR-0101
  - IADR-0102
author: claude
created: 2026-07-26
updated: 2026-07-26
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0022_llm-model-sonnet-5.md (定型RAG回答を Claude Sonnet 5 へ改定・Accepted)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md (グローバル既定を Opus 5 へ改定・Accepted。§決定が他層=Sonnet 5 を明記)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (LLM ゲートウェイ設計・Accepted・本文凍結)"
---

# IADR-0106: `rag-answer` の Sonnet 5 追随

- 状態: Accepted
- 日付: 2026-07-26
- 決定者: claude（実装）

## 起点・関連

- 起点 issue: [#381](https://github.com/endazon/microservices-platform/issues/381)（`enhancement` / `priority:should`）。
  [[IADR-0101]] §フォローアップ 4「`rag-answer` の Sonnet 5 追随が未消化」の消化。
- 計画根拠: [ADR-0022](../../planning/projects/microservices-platform/07_adr/ADR-0022_llm-model-sonnet-5.md)
  が定型・高頻度 RAG 回答の割当を `claude-sonnet-4-6` → **`claude-sonnet-5`** へ改定した（Accepted・2026-07-23）。
  同 ADR §結果のフォローアップが「実装側の `Llm:Routing:PurposeModels` を Sonnet 5 へ更新（IADR 起票）」を求めている。
- [ADR-0025](../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md) §決定も
  「他層（定型RAG回答=**Sonnet 5**、図のコード化=Haiku 4.5、最難関=Fable 5）は変更しない」と記述しており、
  計画側の確定状態は既に Sonnet 5。実装だけが取り残されていた。
- ルーティング設計そのものは [[IADR-0022]]。本 IADR は**その用途別割当のモデル版数のみ**を更新し、
  ルーティング設計・ティア判定・ZDR 除外ロジックは変更しない。
- 仕様書: `docs/specs/20260726_issue-381_rag-answer-sonnet-5.md`。

## コンテキストと課題

設定値 1 行の書き換えに見えるが、見落とすと静かに壊れる論点が 3 つある。

### 1. `Models`（利用許可集合）への登録漏れは無音で失敗する

`LlmRouter.ResolveModel` の用途別解決は `eligible.Contains(purposeModel)` を条件とする。`eligible` は
エンドポイントの `Models` から導出されるため、`PurposeModels` だけを書き換えて `Models` へ登録し忘れると、
**例外もログも出さずに `DefaultModel`（＝`claude-opus-5`）へフォールバック**する。単価もモデルも変わるのに
検知できない。これは [[IADR-0102]] が取引判断のピン留めで実際に踏んだ罠であり、本作業は同じ構造を持つ。

### 2. Sonnet 5 は thinking が既定有効・新トークナイザ

| | Sonnet 4.6 | Sonnet 5 |
| --- | --- | --- |
| `thinking` 省略時 | **思考なし** | **adaptive thinking が有効** |
| `max_tokens` の意味 | 実質「本文の上限」 | **思考トークン＋本文の合算上限** |
| トークナイザ | 従来 | **新トークナイザ（同一テキストで約 +30% トークン）** |
| 標準単価 | $3 / $15 per MTok | **同額**（2026-08-31 まで導入価格 $2 / $10） |
| 非既定サンプリングパラメータ | 可 | **不可**（`temperature` 等は 400） |
| ZDR | 対応 | 対応（30 日保持要件は Fable 5 / Mythos 5 のみ） |

`ClaudeProvider` は `thinking` / `temperature` / `top_p` / `top_k` / assistant prefill を**一切送っていない**
（[[IADR-0101]] の決定）ため、Sonnet 5 で 400 を返す破壊的パラメータは持ち込まれない。差分は上表の
「thinking 既定有効」「新トークナイザ」の 2 点に閉じる。

### 3. ZDR 要件区分での解決結果

`NonZdrModels` は `["claude-fable-5"]` のみ。30 日保持を要求するのは Fable 5 / Mythos 5 であり、
**Sonnet 5 は ZDR 対応**（Sonnet 4.6 と同じ位置づけ）。

## 検討した選択肢

1. **`PurposeModels` と `Models` を同時に更新し、`max_tokens` は据え置く（採用）** — 計画の確定値へ最短で
   追随し、罠（論点 1）をテストで恒久的に塞ぐ。`max_tokens` は [[IADR-0101]] の配分見積もりが Sonnet 5 でも
   成立するため据え置き、実測は #380 に委ねる。
2. `PurposeModels` のみ更新する — 論点 1 により `DefaultModel`（Opus 5）へ黙って落ち、**追随したつもりで
   追随していない**状態になる。#376 / [[IADR-0102]] で実際に踏んだ失敗の再演であり、採らない。
3. あわせて `max_tokens` を引き上げる — 新トークナイザの +30% を根拠に増やす案。ただし本文枠 1024 相当が
   約 1331 相当へ増えても残る思考枠は約 2765 で、[[IADR-0101]] の配分想定の内側に収まる。実測なしに
   引き上げると異常系の 1 応答あたり最大コストだけが上がる。`05_observability-ops` のコスト最適化方針とも
   整合しないため採らない。
4. あわせて `Models` から `claude-sonnet-4-6` を削除する — 割当から外れたモデルを許可集合からも消す案。
   `Models` は「割当」ではなく「利用を許可するモデル集合」であり、削除すると明示 `Model: "claude-sonnet-4-6"`
   を送っている呼び出し側が黙って別モデルへ落ちる（破壊的変更）。追加のみに留める。

## 決定

- `Llm:Routing:PurposeModels.rag-answer` を `claude-sonnet-4-6` → **`claude-sonnet-5`** に更新する。
- claude エンドポイントの `Models`（利用許可集合）へ **`claude-sonnet-5` を追加**する。追加のみとし、
  `claude-sonnet-4-6` は**残す**（明示要求している呼び出し側を壊さないため。`Models` は既に
  `claude-opus-4-8`（取引判断ピン留め）など複数版数を並存させている）。
- **既定 `max_tokens` は 4096 のまま据え置く**（`RagOrchestrator` が明示指定する 2 箇所、共有契約
  `CompletionApiRequest.MaxTokens` の既定、いずれも無変更）。
- **`NonZdrModels` は無変更**（Sonnet 5 は ZDR 対応）。`confidential`/`restricted` × `rag-answer` は
  除外を受けずに `claude-sonnet-5` を選択する。これは Sonnet 4.6 時代と同じ意味であり、T-13 の意図は保たれる。
- **`PurposeModels` の全値が claude エンドポイントの `Models` に含まれること**をテスト（T-19）で恒久的に
  固定する。論点 1 の罠は個別の用途追加のたびに再発し得るため、`rag-answer` 単体ではなく**集合として**守る。
- `thinking` パラメータは引き続き**送信しない**（[[IADR-0101]] の決定を踏襲）。

## 理由

- 計画側（ADR-0022 Accepted・ADR-0025 §決定）が既に Sonnet 5 で確定しており、実装の追随は計画への忠実性の
  問題である。設定駆動のため基盤・契約・API 互換性への影響はない。
- **標準単価が Sonnet 4.6 と同一**のため、定型層の目的（速度とコストのバランス）を変えずに品質を上げられる。
  増えるのは思考分の出力トークンと、新トークナイザによる入出力トークン数の底上げのみで、影響範囲が
  限定的で見積もりやすい。
- `Models` への登録を**同一の決定として束ねる**ことで、論点 1 の罠を「忘れ得る手順」から「決定の一部」へ
  格上げする。さらに T-19 の集合ガードにより、将来 `PurposeModels` へ用途を追加する際も同じ失敗を防ぐ。
- ZDR の位置づけが Sonnet 4.6 と同じであるため、`NonZdrModels` と ZDR 除外フォールバックのロジックは
  無変更で意味が保たれる。

## 結果

- 良い影響: 定型・高頻度 RAG 回答の品質が向上する。単価は据え置き。計画と実装の乖離（[[IADR-0101]]
  フォローアップ 4）が解消し、FR-11 の未決事項が 1 件減る。`Models` 登録漏れの罠がテストで恒久的に塞がれる。
- 悪い影響 / トレードオフ:
  - **thinking が既定で有効になり、出力トークンが増える**（その分コストが増える）。単価は据え置きだが
    総額は上振れし得る。
  - **新トークナイザで同一テキストのトークン数が約 +30%** 増える。可観測性のコスト試算・レート制限
    しきい値・プロンプトキャッシュの最小キャッシュ長のベースラインがずれる（ADR-0022 §結果が予告した
    トレードオフ）。使用量の可視化（Grafana）と月次上限アラートで検知する運用は従来どおり。
  - `max_tokens` 4096 は**実測前の出発値**であり、Sonnet 5 の実トークン消費で不足する可能性は残る。
    不足時は本文が途中で切れ、`stop_reason: "max_tokens"` として観測される（[[IADR-0104]] で判別可能）。
- フォローアップ:
  1. **出力トークンの実測と 4096 の再調整**（[#380](https://github.com/endazon/microservices-platform/issues/380)）。
     Sonnet 5 は thinking 既定有効＋新トークナイザの二重要因を持つため、Opus 5 の実測とは別に計測する。
  2. **新トークナイザ前提でのコスト試算・レート制限しきい値の再測定**（ADR-0022 §結果のフォローアップ）。
     Sonnet 5 は Sonnet 4.x 系とは別のレート制限枠になり得るため、定型層のトラフィック移行で 429 が
     出ないことを確認する。
  3. **縮退経路の「使用モデル」ラベルの見直し**（issue #381「併せて見直す（任意）」・[[IADR-0101]]
     レビューの 🟢1）。`RagOrchestrator` の `AskStreamAsync`（権限なし分岐）と `EmptyAnswer` は LLM を
     呼ばないのに `config["Llm:DefaultModel"] ?? "claude-opus-5"` を使用モデルとして返す。加えて
     `Llm:DefaultModel` というキーは `appsettings.json` に存在せず（実在するのは `Llm:Model`）、常に
     ハードコード値へ落ちている。返却値の変更は応答契約の観測可能な挙動変更であり、かつ「LLM を呼んで
     いない縮退応答が何をモデルとして名乗るべきか」という設計判断（`null` / 空 / 実解決値）を伴うため、
     設定追随の本 PR には混ぜず別 issue で扱う。
  4. **プロンプトキャッシュの最小キャッシュ長の再確認**（ADR-0022 §結果）。新トークナイザによりトークン
     数が変わるため、キャッシュ成立の閾値付近にあるプロンプトは挙動が変わり得る。

## 関連

- Supersedes: なし（[[IADR-0022]] の用途別割当のモデル版数のみ更新。ルーティング設計は不変更）
- Superseded by: なし
- 関連要求 / UC: FR-11（LLM 送信可否の統制）、FR-04（AI 回答と出典）、UC-01
