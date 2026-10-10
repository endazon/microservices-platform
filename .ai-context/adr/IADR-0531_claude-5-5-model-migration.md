---
title: IADR-0531 Claude のモデル割当を 5.5 系（opus-5-5・sonnet-5-5・haiku-5-5）へ切り替える。旧 ID は切り戻し用に残し、haiku-5-5 の単価はプロンプト長の 2 段で持ち、用途別 effort を要求本文へ注入する（既定は rerank=low）。rerank の出力上限は 1024 へ上げる
type: impl-adr
status: Accepted
related_ids: [FR-03, FR-10, FR-11, ADR-0010, ADR-0022, ADR-0025, ADR-0038, ADR-0044, ADR-0127, IADR-0101, IADR-0102, IADR-0112, IADR-0114, IADR-0225, IADR-0340, IADR-0498, IADR-0511, IADR-0528]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0025（既定モデル）
  - planning:projects/microservices-platform/07_adr/ADR-0044（LLM 利用実績と単価表。決定 3）
  - planning:projects/microservices-platform/07_adr/ADR-0038（用途別割当とフォールバック順序）
related_specs:
  - ../specs/20261010_1875_claude-5-5-models.md
---

# IADR-0531: Claude のモデル割当を 5.5 系へ切り替える（#1875）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-10
- 決定者: **利用者（製品の判断）**。裁定 2026-10-10「Claude のモデル割当をすべて 5.5 系へ切り替える」（planning#783）。
  単価の持ち方・effort の注入方式・上限の値・試験の形は claude。
- 採番: 起草時は IADR-0529 だったが、0529 は #1886（#1879）が、0530 は先にマージされる #1889 が取ったため **IADR-0531 へ改番した**
  （採番衝突時は後発が改番する。`.ai-context/adr/README.md` の運用ルール「一意・昇順・欠番なし」）。

## 起点・関連

- 起点: planning#783（利用者裁定 2026-10-10）。MSP 側の実装 issue は **#1875**。
- 計画: ADR-0025（既定モデル）・ADR-0022（定型層）・ADR-0038（用途別割当・鎖）・ADR-0044 決定 3（単価表）・ADR-0127（再順位付け）。
  **計画書は本 IADR の時点で旧 ID のままである。** 反映は planning#783 の (1)・(3) として計画側が行う（本件で新たな環流 issue は起こさない）。
- 前提: IADR-0101（既定 opus・`max_tokens` 4096）・IADR-0102 / IADR-0112（取引判断のピン）・IADR-0114（応答サニタイズ）・
  IADR-0225（鎖）・IADR-0340（一次スクリーニング）・IADR-0498（rerank）・IADR-0511（グラフ用途）・IADR-0528（`AnthropicHttpClient` の生成点）。
- 作業仕様書: [20261010_1875_claude-5-5-models](../specs/20261010_1875_claude-5-5-models.md)（母集合と除外）。

## コンテキストと課題

利用者は 2026-10-10、Claude のモデル割当をすべて 5.5 系へ移すと裁定した（`claude-opus-5` → `claude-opus-5-5`・`claude-sonnet-5` → `claude-sonnet-5-5`・
`claude-haiku-4-5` → `claude-haiku-5-5`。取引判断の 2 層を含む。fable 系は不使用を維持）。提供元の公表値（2026-10-10 確認）は次のとおり。

| モデル | 入力 / 出力（$/MTok） | 既定 effort |
| --- | --- | --- |
| `claude-opus-5-5` | 4 / 20 | medium |
| `claude-sonnet-5-5` | 2 / 10 | high |
| `claude-haiku-5-5` | 入力 ≦100,000 トークン: 0.10 / 0.50、>100,000: 0.50 / 2.50 | medium |

切替に伴う課題は 4 つある。

1. **単価表は 1 段しか持てない。** Haiku 5.5 はプロンプト長で単価が 2 段になる（判定はキャッシュ読み・書きを含む入力トークンの合計で、要求ごと）。
2. **5.5 系は thinking を無効にできず、400 になる要求の形が増えた**（thinking 無効・`budget_tokens`・非既定の sampling・強制 tool_choice・prefill）。
   現行の `ClaudeProvider` はどれも送らないが、送らないことを固定する試験が無い。
3. **思考の量を絞る手段が effort だけになった**が、`Anthropic.SDK` 4.0.0 の `MessageParameters` は `output_config` を持たない。
4. **Haiku 5.5 は既定で考える**（4.5 は考えなかった）。rerank（呼び出し側の期限 8 秒・`max_tokens` 512）と、diagram-coding の鎖の第 2 候補（`max_tokens` 1024）の前提が変わる。

## 決定

### 決定 1 — 全割当を 5.5 系へ。旧 ID は利用許可集合と単価表に残す

`Llm:Model`・`PurposeModels`（12 用途）・`PurposeFallbackModels`（9 用途）・`claude-managed` の `DefaultModel` を 5.5 系へ改める。
`Models`（利用許可集合）には新 3 ID を**追加**し、旧 ID（`claude-opus-5`・`claude-sonnet-5`・`claude-haiku-4-5`。既存の `claude-opus-4-8`・`claude-sonnet-4-6` も）を残す。

- 新 ID を `Models` に足さないと、`LlmRouter.ResolveModel` は例外もログも無く `DefaultModel` へ落ちる（IADR-0102 / IADR-0106 の罠）。
- 旧 ID を残すのは**切り戻し**のためである。`PurposeModels` を戻すだけで戻れ、旧モデルの単価も残るので切り戻した瞬間に費用が「解決漏れ」にならない。
  明示 `Model` で旧 ID を要求する呼び出し側も壊さない。
- `NonZdrModels` は空のまま。`claude-fable-5`・`claude-fable-5-1` は加えない。
- **取引判断の 2 層（`trade-decision`・`trade-decision-screening`）も切り替える。** ピンの Runbook の手順 1（Stage 0 の再実行）は本切替の時点で済んでいない。
  利用者裁定が取引判断の 2 層を明示的に含めたことを根拠に設定を先行させ、**Stage 0 は AST 側の手続きとして planning#783 (3) が追う**。
  実弾解禁は Stage 0 の合格が前提である（従前のピン改定と同じ）。鎖は従前どおり付けない。
- **配備順序**: AST はゲートウェイが返すモデル名を割当表と完全一致で照合する。AST が旧・新の両 ID を受ける段（別 PR）を先に配備し、その後で本変更を配備する。

### 決定 2 — Haiku 5.5 の単価はプロンプト長の 2 段で持つ（上段へ寄せた 1 段にしない）

`ModelPriceEntry` に任意の `LongPrompt { ThresholdInputTokens, InputPerMillionTokens, OutputPerMillionTokens }` を足す。

- **1 要求の入力トークン数が境界を超えた要求だけ**、入力・出力の両方を上段で換算する。境界ちょうどは下段（提供元の「100,000 トークン以下」）。
  段は要求ごとに決まり、集計期間の合計では決めない。換算結果に `LongPromptApplied` を載せる。
- 判定に使う入力トークン数は `usage.input_tokens`。提供元の判定量はキャッシュ読み・書きを含む合計だが、**ゲートウェイはプロンプトキャッシュを使っていない**ので一致する。
  キャッシュを使い始めるときはここを見直す（`ModelPriceTable` のコメントに置いた）。
- 有効期間（区間）は親の `ModelPriceEntry` に従う。段を区間ごとに持てるので、単価改定と段の変更を同じ半開区間の規則で書ける。
- 検証器は境界 < 1（全要求が上段になり静かに過大計上する）と上段の負値を起動時に落とす。
- **採らなかった案**: ①上段の単価の 1 段（大半の要求を 5 倍に過大計上する。過大は警報に出ない —— #1741 で実際に起きた形）。
  ②下段の単価の 1 段（上段の要求を過小に計上する）。③任意個の段のリスト（現に 2 段のモデルが 1 つだけで、汎用化は計画外の抽象化）。

### 決定 3 — 用途別 effort は設定で持ち、`HttpClient` の層で要求本文へ足す（既定は rerank=low だけ）

- 設定: `Llm:PurposeEffort`（用途 → `low` / `medium` / `high` / `xhigh` / `max`）。**既定は `{ "rerank": "low" }`**。書かない用途は effort を送らない（提供元の既定）。
  値域外は `ClaudePurposeEffortOptionsValidator` が起動時に落とす（実行時に送ると全件 400 になり、呼び出し側には上流の失敗としか見えない）。
- 送る相手: effort を受け付けると確かめたモデルだけ（5.5 系の 3 つ・`claude-opus-5`・`claude-sonnet-5`・`claude-opus-4-8`）。`claude-haiku-4-5` は effort を 400 で拒むので、
  **切り戻しで rerank を haiku-4-5 へ戻しても送らない**（設定 1 つで全件 400 にしない）。`claude-sonnet-4-6` は `xhigh` を受けないので値域が揃わず、載せない。
- 注入: `ClaudeProvider` が用途とモデルから effort を決め、`AsyncLocal` の文脈（`AnthropicRequestContext`）に載せて SDK を呼ぶ。
  `AnthropicHttpClient.Create` の鎖の**最も外側**に置いた `AnthropicRequestShapingHandler` が、POST の JSON オブジェクト本文に `output_config.effort` を足す。
  既存の `output_config` は残して `effort` だけ足し、呼び出し側が明示した `effort` は上書きしない。JSON でない本文は触らない。
  ストリームでは要求の送信が反復子の最初の段で起きるので、同じ段で張った文脈が届く。
- `CompletionRequest` に `Purpose`（末尾・既定 null）を足し、`CompletionUseCase` が一括・逐次の両方で渡す。鎖の試行ごとに試行のモデルで effort を決め直す。
- **rerank を low にする理由**: rerank は検索のたびに呼ばれ、呼び出し側の期限が 8 秒と短く、仕事は候補の番号の並べ替え（出力 100 トークン未満）である。
  Haiku 5.5 の既定（medium）の思考は遅延と出力トークンを増やすだけになりやすい。他の用途は提供元の既定に任せる（実測なしに下げない）。
- **採らなかった案**: ① SDK の差し替え（#1749 で別に判断する。effort だけのために移行しない）。② `metadata` 等の既存欄への埋め込み（API の契約外）。
  ③ 要求ごとに `HttpClient` を作る（接続の再利用を失う）。

### 決定 4 — 5.5 系で 400 になる要求キーを送らないことを試験で固定する

`ClaudeProviderRequestShapeTests` が、3 モデル × 非ストリーム・ストリームで実際に出ていく要求本文を捕まえ、`temperature` / `top_p` / `top_k` / `thinking` / `tool_choice` が無いこと、
messages が user の 1 件だけ（prefill が無い）ことを固定する。整形の委譲ハンドラはこれらのキーを足さない。

### 決定 5 — rerank の出力上限の既定を 512 → 1024 へ上げる（期限 8 秒は据え置き）

- `max_tokens` は思考と本文の合算上限である。Haiku 4.5 は考えなかったので 512 は本文（番号 20 個の JSON は 100 トークン未満）だけに使われていたが、
  Haiku 5.5 は考える。effort low でも思考が上限を食うと JSON が切れ、`unparseable` として元の順へ縮退する（検索は止まらない）。上限に余地を持たせる側に倒す。
- 費用の上限は 1 回 1024 × $0.50/1M ≒ $0.0005 で、従前の 512 × $5/1M ≒ $0.0026 より低い。遅延は期限 8 秒が抑える（期限に掛かれば同じく元の順へ縮退）。
- 遅延の実測（期限 8 秒の前提の取り直し）は planning#783 (5) として残る。

### 決定 6 — diagram-coding の出力上限 1024 は据え置く

- 第 1 候補は `claude-sonnet-5-5` で、旧 `claude-sonnet-5` もすでに thinking が既定で有効だった（既定 effort は両方 high）。上限 1024 の前提は第 1 候補では変わらない。
- 新たに考えるようになったのは鎖の第 2 候補（`claude-haiku-5-5`）だけで、鎖は第 1 候補が 400 系で失敗したときしか発火しない。
- 上限に掛かると閉じフェンスが無く「コード化不能」として画像保持へ縮退する（deny-by-default を破らない）。実測なしに上げる根拠が無いので据え置き、
  打ち切り率（`llm.completion.total` の用途 `diagram-coding` のうち終了理由が `max_tokens` の割合）を見て判断する。

## 結果

- 割当・鎖・既定・単価・effort は設定（`appsettings.json`）で持ち、試験（実設定を通す `CompletionRoutingEndpointTests`・`DeployedPriceTableTests`・
  `ClaudeProviderRequestShapeTests.DeployedConfig_SetsRerankLowOnly` ほか）が固定する。
- `AnthropicClient` の鎖は「要求の整形 → 応答のサニタイズ → `HttpClientHandler`」になった（`AnthropicClientWiringTests` が固定）。
- 費用: opus の単価は約 0.8 倍、sonnet は同じ、haiku は 1/10（ただし思考の出力が増える）。月次確認の Runbook に切替の月の読み方を足した。
- 変異で確かめた: 2 段の境界の比較を `>=` にすると境界の行が赤。非ストリームの effort の文脈を外すと effort の試験が赤。

## 残余リスク

- **Stage 0 未実施のまま取引判断の割当が変わる。** 実弾解禁のゲートは AST 側にあり、本 PR はそれを外さない。合格の記録は AST 側に残る。
- **AST より先に配備すると**、AST は返ってくる 5.5 の名を割当と違うと判定する。配備順序を PR 本文に書いた。
- effort の対応モデル集合はコードに持つ。新しいモデルを許可集合へ足すとき、effort に対応するなら `ClaudeEffort.CapableModels` にも足す（足さなければ送らないだけで、壊れはしない）。
- プロンプトキャッシュを使い始めると、Haiku 5.5 の段の判定量（キャッシュを含む入力の合計）と `usage.input_tokens` がずれる。
