---
title: 作業仕様書 — LLM ゲートウェイの Anthropic 呼び出しのタイムアウトを設定で変えられるようにする（既定 100 秒は据え置き。#1872・利用者裁定 2026-10-10）
type: spec
status: done
related_ids: [FR-11, ADR-0010, IADR-0114, IADR-0528]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0010（LLM ゲートウェイ。呼び出し先の集中管理）
issue: "#1872"
---

# 作業仕様書 — Anthropic 呼び出しのタイムアウトの設定化（#1872）

> 本仕様書は実装着手前に作成した（着手 2026-10-10）。判断の記録は **IADR-0528** に置く。
> 基点は MSP `origin/develop` `c0dfd319`。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: **#1872**（ゲートウェイの Anthropic 呼び出しが `HttpClient` の既定 100 秒に固定され、設定で変えられない）。
- 計画: **FR-11**（用途別・機密度別の LLM 送信先切替）・**ADR-0010**（LLM ゲートウェイ）。計画はタイムアウトの値を定めていない（実装の細目）。計画への環流は要らない。
- 関連: AST#243 / AST#1289（AST 側が方針の改訂だけ `max_tokens` を 8192 へ上げた。その呼び出しの期限は 95 秒）・#380（`max_tokens` の再判断）。
- 前提 IADR: [IADR-0114](../adr/IADR-0114_anthropic-unknown-content-block-sanitizing.md)（`HttpClient` へ応答サニタイズの委譲ハンドラを噛ませる構成。本変更はこの構成を変えず、`Timeout` を足すだけ）。

## 裁定の条件（利用者裁定 2026-10-10・issue の提案 1）

| # | 条件 | 写像 |
| --- | --- | --- |
| 1 | 設定で変えられるようにする | 設定キー `Llm:AnthropicTimeoutSeconds`（env `Llm__AnthropicTimeoutSeconds`）。既存の Anthropic 用キー `Llm:ApiKey` / `Llm:Model` と同じ平たい階層に置く |
| 2 | **既定は 100 秒のまま**（挙動を変えない） | 未設定なら `HttpClient.Timeout = 100 秒`（.NET の既定と同値を明示する） |
| 3 | 不正・0・負・数値でない値は既定へ倒す | 起動は止めない。値が在って不正なときだけ warn を 1 行出す |
| 4 | 運用文書に項目と「呼び出し側の期限はこれより短くする」制約を書く | `docs/operations/operations.md` に節を足す。出力トークン実測の Runbook の §5 に `max_tokens` 引き上げとの関係を 1 行足す |
| 5 | helm の values のコメントに載せる（値は置かない） | `deploy/helm/microservices-platform/values.yaml` の `llmgateway.extraEnv` の直前にコメントだけを足す |
| 6 | 既定値と設定の読み取りを試験で固定する | テスト仕様書 FR-11 に **T-33** を足し、`AnthropicHttpClientTests` へ写像する |

## 設計（正は IADR-0528）

1. `LlmGateway/Infrastructure/ExternalServices/AnthropicHttpClient.cs`（新規・静的）:
   - `ConfigKey = "Llm:AnthropicTimeoutSeconds"`・`DefaultTimeoutSeconds = 100`。
   - `ResolveTimeout(IConfiguration, ILogger?)` は文字列を `int.TryParse`（`NumberStyles.Integer`・`InvariantCulture`）で読み、
     1 以上かつ `HttpClient` が受け付ける上限（`int.MaxValue` ミリ秒＝ 2,147,483 秒）以下なら採る。それ以外は既定。
     値が在って採らなかったときだけ warn（キー名・値・既定）を出す。未設定・空は黙って既定。
   - `Create(IConfiguration, ILogger<AnthropicResponseSanitizingHandler>, ILogger?)` は従来の構成（サニタイズの委譲ハンドラ＋
     `AutomaticDecompression = All` の `HttpClientHandler`）に `Timeout` を設定した `HttpClient` を返す。
   - 上限を `HttpClient` の受け付ける値に置くのは、それを超えると `Timeout` の setter が `ArgumentOutOfRangeException` を投げて
     `AnthropicClient` の初回解決（最初の Claude 呼び出し）が落ちるためである（裁定 3「不正は既定へ倒す」の射程に入れる）。運用上の上限は置かない（裁定に無い）。
2. `Program.cs`: `new HttpClient(new AnthropicResponseSanitizingHandler(...){...})` を `AnthropicHttpClient.Create(...)` へ置き換える。
3. 文書: 上記 4・5。`docs/` の表示テキストに修飾付き issue（`AST#…`）と IADR を書かず、trace ブロックへ入れる。

## 母集合（規則 9・10）

- 走査語: `new HttpClient(new AnthropicResponseSanitizingHandler`・`AnthropicClient(`・`Llm__ApiKey`・`Timeout`（LlmGateway 配下）。
  - `AnthropicClient` を生成するのは `Program.cs` の 1 か所だけ（試験は自前で `new HttpClient(handler)` を渡す。試験は本変更の対象外）。
  - ゲートウェイの設定を列挙する文書は無い（issue の言う「設定一覧」は存在しない）。`Llm__ApiKey` を書く文書は Secret の配線の文脈であり、
    タイムアウトの追記先ではない。追記先は `operations.md`（新節）と出力トークン実測の Runbook §5（`max_tokens` の値の場所の節）に絞る。
  - helm で LLM ゲートウェイの env を列挙しているのは `llmgateway.extraEnv` だけ（`deploy/local/values-local.yaml` は値の上書きであり列挙ではない）。
- 新たに誤りになる自分の記述（規則 10）: Runbook §5 の「8192 へ引き上げる候補」は、100 秒の期限と組み合わせると打ち切りが起き得る。
  同節に「引き上げるなら期限も確かめる」を足して塞ぐ。
- 呼び出し側の期限（本変更は変えない）: **［PR #1873 の AI レビュー後に走査し直した。下の「監査の指摘への対応」の表が正］**
  着手時は「AST の方針の改訂 95 秒・検索の再順位付け 8 秒で、いずれも 100 秒より短い」と記憶で挙げたが、95 秒は submodule の pin に未反映で、
  週報・月報の散文（120 秒）を落としていた（規則 9 の違反）。

## 試験（T-33）

| 観点 | 期待 |
| --- | --- |
| 未設定 | 100 秒（`HttpClient` の既定と一致することも併せて固定） |
| 設定値 180 | 180 秒・warn なし |
| `0`・`-5`・`abc`・`1.5`・空白・上限超（`2147484`） | 100 秒。空白以外は warn 1 行 |
| 上限ちょうど（`2147483`） | 採る（`HttpClient.Timeout` へ設定しても例外にならない） |
| `Create` | 返る `HttpClient` の `Timeout` が設定値（未設定なら 100 秒） |

## 監査・AI レビューの指摘への対応（PR #1873・同 PR 内の追記）

- 🟡1 ストリーミングの記述: `HttpClient.Timeout` は逐次 `/complete/stream` では最初の応答ヘッダが届くまでにしか効かない（SDK はヘッダ到着で `SendAsync` を完了し、
  サニタイズの委譲ハンドラは SSE を素通しする。監査の実測: `Timeout` 2 秒・1 秒ごとの SSE 6 件で 7.1 秒正常完了）。IADR-0528 の残余リスクと運用仕様書の「効く範囲」を改め、
  長い出力はストリーミングへ寄せれば頭打ちを避けられることを書いた。
- 🟡2 配線の試験: **T-34**（`AnthropicClientWiringTests`）を足した。実ホストから `AnthropicClient` を解決して期限（`7` → 7 秒・未設定 → 100 秒）とハンドラ鎖を見る。
  生成点のハンドラ鎖（サニタイズ → `HttpClientHandler`・`AutomaticDecompression = All`）も直接見る。
  変異: `Program.cs` を素の `new HttpClient(handler)` へ戻す → 1 本赤（期限 7 秒）。`Create` から応答圧縮を外す → 2 本赤（ハンドラ鎖）。
- 🔴（AI レビュー）呼び出し側の期限の一覧が不正確: 呼び出し側を走査し直した（記憶で挙げない）。
  - 走査: AST は submodule（pin `58fe8c24`）で `git grep` の `LlmGateway|/complete|LlmCompletion` → 呼び出し元 2 サービス（取引判断・報告書）を得て、各 `timeout` の解決を読んだ。
    方針の改訂は AST develop `24dc448b`（AST#1289）の差分も読んだ。MSP は `AddLlmGatewayServiceToken` と gRPC の生成クライアントの登録から呼び出し元を取り、各 `Timeout` / deadline を読んだ。
  - 結果: 再順位付け 8 秒・図のコード化 20 秒・AI 分析／検索チャット・知識グラフ AI 提案・クラスタ要約は期限なし（`HttpClient` 既定 100 秒＝同値）・
    取引判断 30 秒・日報の散文 30 秒・**週報・月報の散文 120 秒（ゲートウェイより長い）**・方針の改訂 60 秒（pin）／95 秒（AST develop `24dc448b` 以降・pin 未反映）。
  - 週報・月報の出力の実測 931 / 1,048 トークンは、AST develop `24dc448b` の IADR-0522（AST 側）の表で確かめた（レビューの数えをそのまま転記していない）。
  - 運用仕様書の節と IADR-0528 決定 4 を表と「呼び出し側の期限 > ゲートウェイの期限ならゲートウェイが先に切る。長い予算を生かすにはこの設定を延ばす」へ書き直した。
- 🟡（AI レビュー）compose の llm-gateway の `environment` に設定の仕方のコメントを足した（値は置かない）。
- 🟢 読む時点: 値は起動時ではなく、シングルトンの初回解決（最初の Claude 呼び出し）で 1 回だけ読む。再起動が要るという結論は変わらない。文言を改めた。

## 受け入れ基準

- [x] Anthropic 呼び出しの HTTP タイムアウトが設定から読まれ、既定値（100 秒）が試験で固定されている。
- [x] 不正値は既定へ倒れ、起動を止めない。
- [x] 運用文書に項目と「呼び出し側の期限はこれより短くする」制約がある。helm の values のコメントに載っている（値は置かない）。
- [x] `dotnet build` / LlmGateway の試験 / `dotnet format --verify-no-changes` / 文書系の node 検査が通る。
