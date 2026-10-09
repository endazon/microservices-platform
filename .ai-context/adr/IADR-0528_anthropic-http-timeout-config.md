---
title: IADR-0528 LLM ゲートウェイの Anthropic 呼び出しの期限（HttpClient.Timeout）を設定 Llm:AnthropicTimeoutSeconds で変えられるようにする。既定は従前と同じ 100 秒、不正値は既定へ倒す
type: impl-adr
status: Accepted
related_ids: [FR-11, ADR-0010, IADR-0114]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0010（LLM ゲートウェイ。呼び出し先の集中管理）
related_specs:
  - ../specs/20261010_1872_anthropic-timeout-config.md
---

# IADR-0528: Anthropic 呼び出しの期限を設定で変えられるようにする（#1872）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-10
- 決定者: **利用者（製品の判断）**。裁定 2026-10-10「設定で可変・既定 100 秒据え置き・不正値は既定」（issue の提案 1）。キー名・上限の扱い・試験の形は claude。

## 起点・関連

- 起点 issue: **#1872**。AST#243 の実測（方針の改訂の出力が 3,805 / 4,096）を受けて AST 側は方針の改訂だけ `max_tokens` を 8192 へ上げた（AST#1289。呼び出しの期限は 95 秒）。その監査で、ゲートウェイの Anthropic 呼び出しが `HttpClient` の既定 100 秒に固定され、設定から変えられないことが分かった。
- 計画: FR-11・ADR-0010。計画はタイムアウトの値を定めていない（実装の細目）。計画への環流は要らない。
- 前提: [IADR-0114](./IADR-0114_anthropic-unknown-content-block-sanitizing.md)（`AnthropicClient` へ応答サニタイズの委譲ハンドラを噛ませた `HttpClient` を渡す構成）。
- 作業仕様書: [20261010_1872_anthropic-timeout-config](../specs/20261010_1872_anthropic-timeout-config.md)（母集合の走査）。

## コンテキストと課題

`Program.cs` は `new HttpClient(new AnthropicResponseSanitizingHandler(...))` を `AnthropicClient` へ渡し、`Timeout` を設定していなかった。
期限は .NET の既定 100 秒であり、設定から変えられない。非ストリーミングの `/complete` は全文の生成を待つため、出力が 50〜80 tok/s なら
8,192 トークンには約 100〜165 秒かかる。呼び出し側が `max_tokens` を上げても、出力の実効上限は約 100 秒 × スループットで頭打ちになる。
100 秒で切れると呼び出し側には `TaskCanceledException` の系統で見え、利用実績は計上されない（プロバイダ側の課金は発生し得る）。

## 決定

### 決定 1 — 設定キーは `Llm:AnthropicTimeoutSeconds`、既定は 100 秒

- Anthropic 向けの既存キー（`Llm:ApiKey`・`Llm:Model`）と同じ平たい階層に置く。env は `Llm__AnthropicTimeoutSeconds`。
  プロバイダ名を入れるのは、ティア A（`Llm:SelfHosted:*`）・ティア C（`Llm:Copilot:*`）は別の `HttpClient` を使い、この値が効かないためである。
- 既定は 100 秒で、従前の暗黙の既定と同値を**明示する**（挙動は変わらない）。`appsettings.json` には置かない（既定はコードの定数が持ち、試験で固定する）。

### 決定 2 — 不正値は既定へ倒し、起動を止めない

- 整数（`InvariantCulture`）で 1 以上 2,147,483 以下だけを採る。0・負・数値でない（`1.5`・`100s` を含む）・上限超は既定へ倒し、warn を 1 行出す。未設定・空白は黙って既定。
- 上限は `HttpClient.Timeout` が受け付ける最大（`int.MaxValue` ミリ秒）である。超えると setter が例外を投げ、`AnthropicClient` の解決（シングルトンの初回解決＝最初の Claude 呼び出し）が落ちる。
  これは裁定の「不正は既定」の射程に入れた。**運用上の上限は置かない**（裁定に無い。延ばしすぎの害は呼び出し側との内外関係であり、値の大小では決まらない）。
- `ValidateOnStart` で起動を止める形（`Llm:Budget` 等の前例）は採らない。裁定が「既定へ倒す」を指定したためである。

### 決定 3 — 生成点を 1 つの静的クラスへ寄せる

- `LlmGateway.Infrastructure.ExternalServices.AnthropicHttpClient`（`ResolveTimeout`・`Create`）が、サニタイズの委譲ハンドラ・応答圧縮・期限をまとめて組む。
  `Program.cs` はそれを呼ぶだけにする。試験は `Create` の返す `HttpClient.Timeout` を直接見る（`AnthropicClient` は渡された `HttpClient` を公開しないため）。

### 決定 4 — 制約「呼び出し側の期限はこの値より短く保つ（長いならこの値を延ばす）」と現行の呼び出し側を運用文書に書く

- どれだけ待つかを決めるのは呼び出し側である（用途・`max_tokens`・利用者の待ち時間を知っているのは呼び出し側だけ）。
  呼び出し側の期限がゲートウェイの期限より長いと、**ゲートウェイが先に切る**。呼び出し側が期限と `max_tokens` を上げても、ゲートウェイの期限が**隠れた上限**になる（本 issue の事象そのもの）。
  よって呼び出し側の期限はゲートウェイの期限より短く保ち、呼び出し側の長い予算を生かすときは、先にこの設定をそれより長く延ばす。
- 現行の呼び出し側（2026-10-10 に呼び出し側のコードを走査した値。記憶で挙げない。PR #1873 の AI レビューの指摘で走査し直した）:
  - MSP: 検索の再順位付け 8 秒（`Rerank__TimeoutSeconds`）・図のコード化 20 秒（`Conversion:DiagramCodingTimeoutSeconds`）・
    AI 分析／検索チャット・知識グラフの AI 提案とクラスタ要約は期限を置いておらず `HttpClient` の既定 100 秒（ゲートウェイと同値）。
  - AST（本リポの submodule の pin `58fe8c24` で走査）: 取引判断 30 秒（`LlmGateway:TimeoutSeconds`）・日報の散文 30 秒・
    **週報・月報の散文 120 秒**（`ReportNarrativeTimeouts` の `HeavyDefault`。REST の `HttpClient.Timeout` も種別の最大＝ 120 秒）・方針の改訂 60 秒。
    方針の改訂の 95 秒は AST develop `24dc448b`（AST#1289）以降であり、**pin には未反映**である。
  - **週報・月報の散文はゲートウェイの既定 100 秒より長い。** この場合はゲートウェイが 100 秒で先に切り、報告書サービスは上流の不調としてプレースホルダの散文へ縮退する。
    PoC の実測では週報・月報の出力は 931〜1,048 トークンで、現状は 100 秒に届いていない。本 PR は既定を変えない（裁定）ため、この関係は運用文書に明記し、延ばす判断は運用者へ委ねる。
- 記載先: `docs/operations/operations.md`（新節）・出力トークン実測の Runbook §5（`max_tokens` を上げるときは期限も確かめる）・helm `values.yaml` の `llmgateway.extraEnv` のコメント（値は置かない）。

## 結果

- 既定のままなら挙動は変わらない（T-33 が 100 秒と .NET の既定の一致を固定する）。
- 運用者は `Llm__AnthropicTimeoutSeconds` で延ばせる（週報・月報の散文の 120 秒を生かすには 120 秒より長くする）。延ばしたことで長い出力が通るかは、呼び出し側の期限とスループット次第である。

## 残余リスク

- ストリーミング経路（`/complete/stream`）も同じ `HttpClient` を使うが、期限が掛かるのは**最初の応答ヘッダが届くまで**である。SDK のストリーミング経路は応答ヘッダの到着で `SendAsync` を完了させ、サニタイズの委譲ハンドラも SSE（`text/event-stream`）を素通しするため、以後の本文の読み取りに `HttpClient.Timeout` は効かない（PR #1873 の監査の実測: `Timeout` 2 秒・1 秒ごとの SSE 6 件が 7.1 秒で正常に完了した。従前も同じ）。
  したがって**長い出力をストリーミング経路へ寄せれば、この期限の頭打ちは避けられる**（issue の提案 3）。本 IADR はその移行を採っていない（呼び出し側の変更が大きい）が、延ばす以外の逃げ道として残る。
- 呼び出し側の期限との内外関係は機械検査していない（呼び出し側は別リポ・別設定にある）。運用文書の制約が唯一の手段である。
