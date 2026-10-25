---
title: IADR-0641 LLM ゲートウェイは Claude のエンドポイントが有効なのに API キーが空なら起動時に止まる。鍵なしで動かす検証スタックは Llm:AllowMissingApiKey=true を明示する
type: impl-adr
status: Accepted
related_ids: [FR-11, ADR-0010, IADR-0528, IADR-0562]
author: claude
created: 2026-10-26
updated: 2026-10-26
related_specs:
  - ../specs/20261026_2136_llm-api-key-fail-fast.md
---

# IADR-0641: Claude の API キーが空なら起動時に止める（#2136）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-26
- 決定者: claude（#2136。依頼はコーディネーター 2026-10-26）

## 起点・関連

- 起点 issue: #2136。
- 関連: [IADR-0562](./IADR-0562_anthropic-official-sdk-migration.md)（公式 SDK と生成点 `AnthropicClientFactory`）、[IADR-0528](./IADR-0528_anthropic-http-timeout-config.md)（同じ生成点の期限）。
- 作業仕様書: [20261026_2136_llm-api-key-fail-fast](../specs/20261026_2136_llm-api-key-fail-fast.md)。

## コンテキストと課題

- `AnthropicClientFactory.Create` は `Llm:ApiKey` をそのまま SDK へ渡す（null は `placeholder`）。起動時には何も言わない。
- クライアントは遅延シングルトンで、最初の Claude 呼び出しのときに初めて作られる。空のキーはそこで 401 になって初めて分かる。readiness は通ったままである。
- Helm（本番の既定）は Secret の欠落では Pod を作らない。しかし値が空の Secret は通る。
- 一方で、鍵なしで動かす環境がある。compose・ローカル k8s（PoC・CI の使い捨てスタック）・`dotnet run` である。どれも「空＝外部 LLM を呼ばない」を fail-safe として扱ってきた。

## 決定

### 決定 1: Claude のエンドポイントが有効で鍵が空なら、起動時に止める

- 条件は 2 つの積である。
  - `Provider == "claude"` かつ `Enabled` のエンドポイントが 1 つ以上ある。
  - `Llm:ApiKey` が null・空・空白である。
- 検証は `ClaudeApiKeyStartupValidator`（`IValidateOptions<LlmRoutingOptions>`・`ValidateOnStart`）で行う。既存の `EmbeddingRoutingOptionsValidator` と同じ作りである。
- Claude のエンドポイントがすべて無効なら検証しない。鍵を使う経路が無いからである。

### 決定 2: opt-out は `Llm:AllowMissingApiKey=true` の明示だけ

- 値が `true` のときだけ効く。置いていない・`true` 以外（`yes`・`1` など）は opt-out にしない（fail-closed）。
- opt-out で鍵なしのまま起動したときは、warn を 1 行出す。
- 置く先は鍵なしで動かす環境だけである: compose・`deploy/local/values-local.yaml`・`appsettings.Development.json`。
- 本番の既定（`values.yaml`・`appsettings.json`）には置かない。
  - `scripts/helm-llm-provider-keys.test.js` が、opt-out が本番像の描画に現れず、values-local の描画に `"true"` で 1 回だけ現れることを見る。
  - 値が空の Secret で起動すると、Pod は起動に失敗する。
  - ローリング更新なら、古い Pod は動き続ける（新しい Pod が ready にならない）。

### 決定 3: 鍵の値はログにも例外にも出さない

- 失敗の文言と warn は、設定キーの名前とエンドポイント名だけを含む。値を差し込む書き方をしない。
- 試験は、探り値の鍵がどのログにも現れないこと、空白だけの鍵が失敗の文言に現れないことを確かめる（T-46）。

### 決定 4: 生成点の `?? "placeholder"` は変えない

- opt-out の下での挙動（最初の呼び出しの 401）を変えないためである。

## 検討した案

| 案 | 採否 | 理由 |
| --- | --- | --- |
| 既定で止め、検証スタックが明示で外す（本決定） | 採る | 本番の既定が安全側になる。外す側は名前で分かる |
| 既定で起動し、本番だけが明示で止める | 採らない | 本番の設定を書き忘れると、今と同じく 401 まで分からない |
| `ASPNETCORE_ENVIRONMENT` で分ける | 採らない | compose・values-local は本番と同じ環境名で動く。環境名と鍵の有無は別の軸である |
| 検証スタックでは Claude のエンドポイントを無効にする | 採らない | 用途別のルーティングとフォールバックの挙動が本番と変わる。使い捨てスタックの試験が本番の形を見なくなる |

## 結果

- 本番の既定で鍵が空なら、起動の段で分かる。
- 検証スタックの挙動は変わらない（opt-out の下で、最初の呼び出しが 401）。
- 残余: セルフホスト・Copilot の資格情報は検証しない（既定でエンドポイントが無効。本件と別）。
