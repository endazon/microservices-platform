---
title: IADR-0677 LLM ゲートウェイは起動時に経路の設定（未知のモデル・空の宛先・単価の欠落）を 1 か所で引き、段 1 は Warning、段 2（Llm:RouteValidation:Mode=Fail）で起動を止める（#2411）
type: impl-adr
status: Accepted
related_ids: [NFR-18, FR-11, FR-10, ADR-0010, ADR-0044, IADR-0641, IADR-0466, IADR-0531]
author: claude
created: 2026-11-05
updated: 2026-11-05
related_specs:
  - ../specs/20261105_2411_llm-route-startup-check.md
---

# IADR-0677: LLM ゲートウェイの経路の設定の起動時検査（#2411）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: claude（#2411。コーディネーターの指示 2026-11-05）

## 起点・関連

- 起点 issue: #2411。
- 前提: 計画 ADR-0010（呼び出し先の切り替え）・ADR-0044 決定 3（無音で 0 円にしない）。IADR-0641（鍵の起動時検査。同じ ValidateOnStart の機構）。
- 作業仕様書: `.ai-context/specs/20261105_2411_llm-route-startup-check.md`。

## コンテキスト

- 用途の割当に宣言の無いモデルを書くと、`LlmRouter.ResolveModel` は黙って既定のモデルへ落ちる。
- 有効にしたエンドポイントの宛先（`Llm:SelfHosted:BaseUrl` 等）が空でも起動し、最初の呼び出しの例外で初めて分かる。
- 単価の欠落は呼び出しのたびの警告と `llm.pricing.unpriced.total` で表に出るが、金額は計上から抜ける（起動時には分からない）。

## 決定

### 決定 1 — 検査は純粋な関数 `LlmRouteConfigurationCheck.Find` の 1 か所に置き、段 1 の警告と段 2 の拒否が同じ結果を使う

| 種類（kind） | 誤りとするもの |
| --- | --- |
| `unknown_model` | `PurposeModels`・`PurposeFallbackModels` の値が、どのエンドポイントの `Models` にも無い。エンドポイントの `DefaultModel` がそのエンドポイントの `Models` に無い |
| `destination` | 有効なエンドポイント（生成・埋め込み）のプロバイダが未知。宛先の設定（`Llm:SelfHosted:BaseUrl`・`Llm:Copilot:BaseUrl`・`Embedding:Voyage:BaseUrl`・`Embedding:SelfHosted:BaseUrl`）が空、または http(s) の絶対 URL ではない |
| `missing_price` | 有効な外部エンドポイント（`claude`・`copilot`）の各モデルに、起動時点で有効な単価の区間が無い |

- Claude の宛先は `AnthropicClientFactory` が本番の URL に固定しているので見ない。決定的ローカル埋め込みは宛先を持たない。
- セルフホストのモデルは社外の費用を持たないので、単価を要求しない。
- 未知のモデルの判定は「宣言（全エンドポイントの `Models`）」に対して行う。有効・無効は問わない（無効なエンドポイントのモデルを割り当てた場合の越境の判定はルータの責務のまま）。

### 決定 2 — 段は `Llm:RouteValidation:Mode`

- 未設定・`Warn`（段 1・既定）: 起動を止めない。起動後に誤り 1 件ごとに Warning を 1 行出す（`LlmGateway.Startup`）。
- `Fail`（段 2）: `IValidateOptions<LlmRoutingOptions>`（`LlmRouteStartupValidator`）が失敗を返し、ValidateOnStart で起動を止める。
- 段の値が不正なら起動を止める（「止めたつもりで警告だけ」へ黙って倒さない）。

## 結果・残余リスク

1. 段 1 のあいだは、誤りがあっても起動する（ログを見ない限り分からない）。段 2 への切り替えは、稼働のログで Warning が 0 件であることを確かめてから行う。
2. 単価の金額の妥当性は見ない（区間の有無だけを見る）。
3. 起動時点の検査である。稼働中に単価の区間が切れる場合は、従来どおり呼び出しのたびの警告と `llm.pricing.unpriced.total` が表に出す。
