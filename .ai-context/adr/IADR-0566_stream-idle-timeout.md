---
title: IADR-0566 LLM ゲートウェイの逐次呼び出し（Claude 経路）にイベントの間の無通信の期限 Llm:AnthropicStreamIdleTimeoutSeconds（未設定は Llm:AnthropicTimeoutSeconds の解決値）を持たせ、期限切れは TimeoutException として upstream_error（上流状態 transport）へ縮退させる
type: impl-adr
status: Accepted
related_ids: [FR-11, FR-04, NFR-02, ADR-0010, IADR-0562, IADR-0528, IADR-0374, IADR-0037, IADR-0101]
author: claude
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0010（LLM ゲートウェイ）
related_specs:
  - ../specs/20261020_1997_stream-idle-timeout.md
---

# IADR-0566: 逐次呼び出しにイベントの間の無通信の期限を持たせる（#1997）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-20
- 決定者: claude（#1997。オーケストレーターの指示「呼び出し側の全体の期限を実測し、足りなければ実装する」）

## 起点・関連

- 起点 issue: #1997（Refs #1749）。
- 関連する計画書 ID: FR-11・FR-04・NFR-02。計画 ADR: ADR-0010。
- 改める実装判断: [IADR-0562](./IADR-0562_anthropic-official-sdk-migration.md) 決定 4 のうち「逐次は応答ヘッダの到着より後を呼び出し側の取り消しだけが止める」。全体に期限を掛けない判断は変えない。
- 引き継ぐ実装判断: [IADR-0528](./IADR-0528_anthropic-http-timeout-config.md)（設定の値の規則）・[IADR-0374](./IADR-0374_llm-upstream-status-metric-axis.md)（上流状態の軸）。
- 作業仕様書: [20261020_1997_stream-idle-timeout](../specs/20261020_1997_stream-idle-timeout.md)（呼び出し側の全体の期限の実測の表・既定値の根拠・変異）。

## コンテキストと課題

IADR-0562 決定 4 は、一括の呼び出しの全体に期限を掛け、逐次には掛けなかった。長い出力が長く流れ続けるのは正常だからである。
その結果、応答ヘッダが届いた後に上流からの受信が止まると、ゲートウェイは呼び出し側が取り消すまで待ち続ける。

呼び出し側が全体の期限を持っていれば、それで足りる。作業仕様書の表のとおり実測すると、逐次の唯一の呼び出し元（AI 分析の RAG の回答）から
ブラウザまでの段は、どれも全体の期限も無通信の期限も持たない。BFF の `HttpClient` の 100 秒は応答ヘッダの到着までしか効かない。
AI 分析は LLM を呼ぶ前に出典を書いてヘッダを返す。AST は逐次を呼ばない。止まった上流は、利用者が「停止」を押すまで要求を握り続ける。

## 検討した選択肢

1. **持たせない**（現状）。呼び出し側に期限が無いので、止まった上流は残り続ける。採らない。
2. **呼び出し側（AI 分析・BFF）に全体の期限を足す**。長い正当な回答を切る。全体の長さの上限は回答の質の統制に当たり、実装側で決める値ではない。採らない。
3. **ゲートウェイの Claude 経路で、イベントの間の無通信に期限を掛ける**。長く流れ続ける応答は切らず、止まった上流だけを切る。採る。
4. HTTP の読み取り（バイト）単位で期限を掛ける（SDK が飲み込む `ping` も数えられる）。SDK の内側の HttpClient に手を入れることになり、一括の経路と共有するハンドラの形が変わる。3 で既定値の根拠が立つので採らない。

## 決定

### 決定 1 — 次のイベントを待つ間だけ時計を回す

- `ClaudeProvider.StreamAsync` は、SDK の列挙子の `MoveNextAsync` の前に期限を掛け、イベントを受け取ったら止める。時計を止めるので、`yield` の先（gRPC への書き込み）が遅くても数えない。
- 数えるのは SDK が返すイベント（`message_start`・`content_block_*`・`message_delta`・`message_stop`）である。SDK が飲み込む `ping` は数えない（決定 3 の既定値は、`ping` が無くても足りる長さにする）。
- 最初の `MoveNextAsync`（要求の送信と応答ヘッダの到着）にも掛かる。そこには既に `Llm:AnthropicTimeoutSeconds` が掛かっていて、既定ではどちらも同じ値である。

### 決定 2 — 期限切れは TimeoutException。縮退は既存の upstream_error

- 期限切れは `TimeoutException` で上げる（`OperationCanceledException` にしない）。`CompletionUseCase` は従前どおり `OperationCanceledException` 以外を `upstream_error` へ縮退する。最終イベントは `Sent=false`・`FailureKind=upstream_error` になる。
- 計器は新設しない。補完の計器の上流状態は `transport` になる（IADR-0374 の分類。`TimeoutException` は輸送の失敗に入る）。原因はゲートウェイのエラーログの例外のメッセージ（設定キーの名前を含む）で分かる。
- 呼び出し側の取り消しは、従前どおり `OperationCanceledException` で伝える（期限切れと区別する）。
- 期限切れも再試行しない（IADR-0562 決定 3）。既に流した本文は撤回できない（IADR-0104 と同じ制約）。

### 決定 3 — 設定キーと既定値

- 設定キーは `Llm:AnthropicStreamIdleTimeoutSeconds`（env `Llm__AnthropicStreamIdleTimeoutSeconds`）。
- 未設定・空白のときは `Llm:AnthropicTimeoutSeconds` の解決値に従う（既定 100 秒）。1〜2,147,483 の整数は採る。不正値は同じ解決値へ倒し、warn を 1 行出す（IADR-0528 決定 1・2 と同じ規則）。
- 既定値の根拠（作業仕様書 §既定値の根拠）:
  - 逐次の利用者は `rag-answer` だけで、`max_tokens` は 4,096（思考と本文の合算）である。
  - 50〜80 トークン／秒なら、全部を思考に使っても 51〜82 秒で終わる。イベントの間の無通信は、生成の全体より短い。
  - 同じ用途の一括は、全体を 100 秒の内に終える前提で運用している。
- 一括の期限を延ばすと、無通信の期限もそれに付いて延びる。

## 結果

- 止まった上流は、既定で 100 秒の無通信の後に切れる。最終イベントで利用者に「呼び出し先が現在利用できません」が返り、ゲートウェイ・AI 分析・BFF の要求が解放される。
- 長い応答は、イベントが流れ続ける限り切れない。
- 固定: T-45（`ClaudeProviderStreamIdleTimeoutTests`。変異 3 種で赤）。

### 残るもの

- `ping` を数えない。思考の文字列を流さない応答で、思考が既定値を超えて長い用途を逐次で使い始めたら、既定値か決定 1 の数え方を見直す（`max_tokens` を上げるときに併せて確かめる）。
- 呼び出し側（AI 分析・BFF）は、全体の期限を引き続き持たない（選択肢 2 は計画の統制値の判断である）。

## 関連

- #1997・#1749
