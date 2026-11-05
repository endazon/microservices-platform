---
title: 作業仕様書 — LLM ゲートウェイの経路の設定（モデルの割当・上流の宛先・単価表）を起動時に検査する（段 1 は警告・段 2 で起動を止める。#2411）
type: spec
status: done
related_ids: [NFR-18, FR-11, FR-10, ADR-0010, ADR-0044, IADR-0641, IADR-0674]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#2411"
---

# 作業仕様書 — LLM の経路の設定の起動時検査（#2411）

> 本仕様書は、実装に着手する前に作成した（着手 2026-11-05。`origin/develop` `170f1efb0`）。

## 事象（現物で確かめたこと）

- `LlmRoutingOptions` の起動時の検証は、Claude の鍵の有無（`ClaudeApiKeyStartupValidator`）だけである。
- `LlmRouter.ResolveModel` は、用途のモデルがエンドポイントの `Models` に無いと、黙って既定のモデルへ落ちる。
- `SelfHostedProvider`・`CopilotProvider`・`SelfHostedEmbeddingProvider` は、宛先が空だと呼び出しの時点で例外を投げる（起動は通る）。
- `ModelPricingOptionsValidator` は区間の重なりと負値を見るが、使うモデルに単価があるかは見ない。

## 方針（IADR-0674）

- 誤りの 3 種（未知のモデル・空の宛先・単価の欠落）を純粋な関数 1 つで引く。
- 段 1（既定）は起動時の Warning、段 2（`Llm:RouteValidation:Mode=Fail`）は起動を止める。

## 受け入れ基準と試験

- [x] 段 2 では、3 種のそれぞれが起動を止め、理由に対象（モデル名・設定キー）を名指しする（`LlmRouteStartupCheckHostTests`。develop の上で 3 件が赤であることを確かめてから実装した）。
- [x] 既定の段では、同じ誤りがあっても起動する（同上）。
- [x] 既定の設定（`appsettings.json`）は段 2 でも誤りが無い（同上）。
- [x] 無効なエンドポイントの宛先は見ない。セルフホストのモデルに単価を要求しない。埋め込みの宛先も見る（`LlmRouteConfigurationCheckTests`）。
- [x] 段の値が不正なら起動を止める（同上）。

## 母集合（規則 9・10。着手前に引いた）

- 宛先の設定キー: `grep -rn 'config\["' src/platform/backend/Services/LlmGateway/Infrastructure/ExternalServices` → `Llm:SelfHosted:BaseUrl`・`Llm:Copilot:BaseUrl`・`Embedding:Voyage:BaseUrl`・`Embedding:SelfHosted:BaseUrl`。Claude は `AnthropicClientFactory` が本番の URL に固定する。
- プロバイダの名前: `Program.cs` の `AddKeyedSingleton` → 生成 `claude`・`selfhosted`・`copilot`、埋め込み `voyage`・`selfhosted-embedding`・`deterministic-embedding`。
- 配備の上書き: `deploy/helm`・`deploy/docker-compose.yml` の `Llm__`・`Embedding__` を引いた。有効にする経路（Voyage・セルフホストの埋め込み）は宛先も一緒に注入している。段 1 の既定では起動は変わらない。

## 対象外

- 単価の金額の妥当性。段 2 への切り替え（稼働の設定。所有者が行う）。
