---
title: IADR-0114 Anthropic 応答の未知 content ブロックを許可リストで除去し、SDK 側の fail-closed を止める
type: impl-adr
status: Accepted
related_ids:
  - FR-04
  - FR-11
  - ADR-0010
  - ADR-0025
  - IADR-0022
  - IADR-0037
  - IADR-0101
  - IADR-0104
  - IADR-0112
  - IADR-0113
author: claude
created: 2026-08-01
updated: 2026-08-01
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (LLM ゲートウェイ設計・Accepted・本文凍結)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md (グローバル既定を Opus 5 へ改定・Accepted)"
---

# IADR-0114: Anthropic 応答の未知 content ブロックを許可リストで除去する

- 状態: Accepted
- 日付: 2026-08-01
- 決定者: claude（実装）／利用者（[ai-stock-trading#290](https://github.com/endazon/ai-stock-trading/issues/290) の調査依頼で選択肢 A/B/C を提示され **A** を採用）

## 起点・関連

- 起点 issue: [ai-stock-trading#290](https://github.com/endazon/ai-stock-trading/issues/290)。
  同 issue の調査中に、**issue 本体とは別系統かつより重篤な**本欠陥を実測で確定させた。
  #290 が観測する rationale（`解析不能または見送り`）は本欠陥からは出ない（§結果「#290 との関係」）ため
  issue は閉じず、`Refs` で参照する。
- 直前の実装判断: [[IADR-0113]]。同 IADR は残作業として **「live 実原因の確定（稼働環境不触のため未確認）」**
  を挙げ、live の `Sent=false` を「プロバイダ未登録／**呼び出し例外**」の別分岐と推定していた。
  本 IADR はその**呼び出し例外の正体**（`JsonException: Unknown type thinking`）を確定させる。
- 仕様書: `docs/specs/20260801_issue-290_thinking-content-block.md`。
- 割当モデル・ルーティング（[[IADR-0112]] / [[IADR-0113]]）、リクエスト側パラメータ方針（[[IADR-0101]]）、
  終了理由の契約と本文破棄（[[IADR-0104]]）は変更しない。

## コンテキストと課題

`ClaudeProvider.CompleteAsync` は `Anthropic.SDK` 4.0.0 の `GetClaudeMessageAsync` を使う。同 SDK の
`ContentConverter` は content ブロックの判別子を**列挙で分岐**し、未知の型で例外を投げる。本 PR で
`AnthropicClient` にスタブ `HttpClient` を渡して実測した結果:

| `content[].type` | SDK 4.0.0 の挙動 |
| --- | --- |
| `text` / `image` / `tool_use` / `tool_result` | 解析できる |
| `thinking` | **`System.Text.Json.JsonException: Unknown type thinking`** |
| `redacted_thinking` | `JsonException: Unknown type redacted_thinking` |
| `server_tool_use` | `JsonException: Unknown type server_tool_use` |

未知型が **1 個でも**混ざると配列全体＝**応答全体**が失われる（部分的な劣化ではない）。

一方、現在の割当モデルは [[IADR-0112]] / [[IADR-0113]] により
`claude-opus-5`（`report-weekly` / `report-monthly` / `default`）・
`claude-sonnet-5`（`trade-decision` / `report-daily` / `rag-answer`）・
`claude-fable-5`（`analysis`）であり、**いずれも thinking（拡張思考）が既定で有効**（fable-5 は常時有効で
無効化不可）である。[[IADR-0101]] は `MaxTokens` を思考込みで見積もる対処を入れていたが、
**応答の content に thinking ブロックが載ること自体**は誰も扱っていなかった。

結果として非ストリーミング `/complete` は**断続ではなく決定的に全件失敗**する。`CompletionEndpoints` が
例外を握って縮退応答（`Sent=false`）を返す設計のため、500 にもならず「呼び出し先が現在利用できません」に
見える。AST 側では実 LLM 経路が成立せず、取引判断も報告書散文も常に安全既定（Hold／プレースホルダ）へ倒れる。

**ストリーミング（`/complete/stream`）は壊れていない**ことも実測で確認した。SSE 経路（[[IADR-0037]]）に
`content_block_start`(thinking)・`thinking_delta`・`signature_delta` を流しても例外は発生せず、本文の
`text_delta` は正常に取り出せる。よって是正対象は非ストリーミング経路のみである。

## 検討した選択肢

1. **(A) 未知 content ブロックを解析前に除去する（採用）**
   `AnthropicClient` へ渡す `HttpClient` に委譲ハンドラを挟み、JSON 応答の `content[]` を
   **既知型の許可リスト**へ絞ってから SDK のデシリアライズへ渡す。
2. **(B) リクエストで拡張思考を無効化する（棄却）**
   SDK 4.0.0 の `MessageParameters` に `thinking` を表現する口が無い。仮に送れても、`claude-fable-5` は
   無効化指定自体が 400、`claude-opus-5` は effort `xhigh`/`max` との併用が 400 で、値域が
   モデルごとに割れる。さらに thinking 無効時には「ツール呼び出しが本文テキストとして書かれ実行されない」
   「`<thinking>` タグが本文へ漏れる」という既知の劣化があり、取引判断の品質にも影響する。
   なにより**未知型一般への耐性が付かない**（`server_tool_use` は thinking と無関係に落ちる）。
3. **(C) `Anthropic.SDK` を thinking 対応版（5.x）へ更新する（棄却・少なくとも主対策としては）**
   5.10.0 まで公開されており thinking 型は入るが、**判別子を列挙して未知型で投げる構造は変わらない**
   （4.0.0 が `server_tool_use` ですら落ちることがその証拠）。次に API が新しいブロック型を足した時点で
   同じ障害が再発する。加えて共有基盤の依存更新は LlmGateway 以外への影響確認と回帰コストを伴う。
   thinking 本文（`display: "summarized"`）を**活用したくなった時点**で、独立した PR として扱うのが妥当。

## 決定

**決定1: 許可リスト方式のサニタイズを入れる。**
`AnthropicContentBlockSanitizer`（純関数）が `content[]` から `text` / `image` / `tool_use` /
`tool_result` **以外**を除去する。拒否リスト（`thinking` を名指しで落とす）ではなく許可リストにするのは、
**将来 API が追加する未知型へ更新なしで耐える**ためである。型名を列挙しない設計そのものが fail-safe の本体。

許可リストの基準は「**SDK のデシリアライザが認識する型の全体**」であって「アシスタント応答に現れる型」
ではない。`image` / `tool_result` は主にリクエスト側の型で応答には通常現れないが、判定基準を SDK 側に
揃えておくほうが、応答での出現有無という別の推測を持ち込まずに済む（現れなければ使われないだけで実害はない）。

**決定2: 接合点は `HttpClient` の委譲ハンドラに置く。**
`Anthropic.SDK` は content の多態デシリアライズを自前の `JsonSerializerOptions` で完結させており、
外部から変換器を差し込む口が無い。SDK を差し替えずに耐性を持たせられる唯一の接合点が応答本文の通過点である。
`ClaudeProvider` 側のロジックは 1 行も変えない。

一次ハンドラは既定の `HttpClientHandler` を使う（システムプロキシ設定は既定で引き継がれる）。応答圧縮だけは
SDK 既定の内部クライアントの設定に依存しないよう `AutomaticDecompression` を明示的に有効化する。

**決定3: 対象を 2xx かつ `application/json` に限る。**
非 2xx は SDK 側の例外整形へ委ねる（エラー本文を書き換えない）。SSE（`text/event-stream`）は
実測で壊れていないうえ、サニタイズするとストリームの全量バッファリングという遅延要因を持ち込むため触らない。

**決定4: 既知型だけの応答は書き換えない。**
除去対象が 0 個なら本文へ一切触らず素通しする。「サニタイズ自体が本文を変える」経路を既定で持たないため。

**決定5: 除去したブロック型は必ず WARN で記録する。**
無言で捨てるとモデル側の応答形状の変化に気づけないまま本文が空になり得る。ログには**型名のみ**を出し、
本文は載せない（プロンプト・応答の全量記録は呼び出し側の `LogPrompts` の管轄であり、既定オフの
安全側設定をゲートウェイ側から迂回させない）。

**決定6: 全ブロックが未知なら content 空へ縮退する（例外にしない）。**
呼び出し側は「空応答」として既存の安全既定（AST では Hold／プレースホルダ）で扱える。
未知型の存在そのもので取引判断を落とすより、既存の空応答分岐へ合流させるほうが挙動が読める。

## 理由

- 本欠陥の本質は「**未知の 1 ブロックが応答全体を道連れにする**」ことであり、thinking はその最初の実例に
  すぎない。したがって是正は「thinking を通す」ではなく「未知型で全体を落とさない」に置くべきである。
- 許可リストは、API 側が増やす自由（新ブロック型の追加）とこちら側の安定性を分離する。拒否リストや
  SDK 更新は、API が動くたびにこちらの更新を要求する。
- SDK に手を入れない・`ClaudeProvider` を変えないことで、ルーティング・機密区分・終了理由・
  メトリクスといった既存の決定（[[IADR-0104]] / [[IADR-0109]] / [[IADR-0110]] / [[IADR-0112]] /
  [[IADR-0113]]）に一切干渉しない。

## 結果

- 非ストリーミング `/complete` が thinking 既定有効モデルで成立する。取引判断・報告書散文の実 LLM 経路が
  復帰する（**本欠陥の下では実 LLM 経路は全件不成立だった**）。
- **#290 との関係（issue の前提の訂正）**: 本欠陥が起きたとき AST 側は `Sent=false` 分岐へ入り、rationale は
  `LLM ゲートウェイ送信不可のため見送り` になる。#290 が報告する `解析不能または見送り` は
  `TradeDecisionParser` / `DecisionAggregator` 由来の別文言であり本欠陥からは出ない。すなわち
  **#290 の 8 分岐のうち本欠陥に起因するものは無い**。両者は独立した障害で、#290 側（可観測性と
  パーサ堅牢化）は AST 側で別途消化する。
- トレードオフ:
  - thinking の要約本文は**取得できない**（落としている）。将来これを表示・監査したくなったら
    選択肢 (C) の SDK 更新が必要になる。現時点の呼び出し側はいずれも本文テキストしか読まない。
  - JSON 応答を 1 度文字列として読むため、書き換えが発生する応答では本文が再直列化される
    （キー順は保持。日本語は過剰エスケープしない）。既知型のみの応答は決定4 により素通しで、
    再直列化の影響を受けない。
  - SSE は対象外のため、将来 SDK のストリーム経路が未知型で落ちるようになった場合は別途対処が要る
    （現時点では実測で壊れていない）。
- 回帰は **T-24**（`AnthropicContentBlockSanitizerTests` / `ClaudeProviderThinkingTests`・計 19 ケース）で固定。
  **変異テスト**（サニタイズを外すと同じ応答で `Unknown type thinking` になる）を常設し、
  ガードが load-bearing であることを恒久的に証明する。
- 稼働中環境への操作なし。設定・割当・実弾に関わる既定は不変。

## 関連

- 仕様書: `docs/specs/20260801_issue-290_thinking-content-block.md`
- 機能仕様: `docs/functional/FR-11_llm-egress-routing.md`（応答 content ブロックの未知型除去）
- テスト仕様: `docs/tests/FR-11_llm-egress-routing.md`（T-24）
- 残: #290 本体（AST 側）、AST の縮退ログ文言是正
  （[ai-stock-trading#315](https://github.com/endazon/ai-stock-trading/issues/315)）、
  thinking 本文を活用する場合の SDK 更新検討、live 環境での実地確認。
