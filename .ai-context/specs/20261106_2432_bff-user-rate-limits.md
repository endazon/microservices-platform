---
title: 作業仕様書 — BFF の LLM を伴う口に、利用者ごとの要求数の上限を掛ける（段 1 は報告・段 2 で 429。#2432）
type: spec
status: done
related_ids: [NFR-18, ADR-0032, ADR-0044, IADR-0686]
author: claude
created: 2026-11-06
updated: 2026-11-06
issue: "#2432"
---

# 作業仕様書 — BFF の利用者ごとの要求数の上限（#2432）

> 本仕様書は、実装に着手する前に作成した（着手 2026-11-06。`origin/develop` `17565cd0b`）。

## 事象

- BFF にレート制限は無い（`grep -rn -i RateLimit src/platform/backend/Bff src/knowledge/backend/Bff` は 0 件。エッジの設定にも無い）。
- `GraphBffEndpoints.cs` の注記にも「BFF に LLM 経路の流量制限は無い」とあった（本 PR で改めた）。
- LLM ゲートウェイの上限は呼び手のサービス単位であり、利用者 1 人の量は区別しない。

## 方針（IADR-0686）

- 共有の門 `UserRateGate.LimitPerUserOrReport(群, 既定)`（`Platform.Shared.Infrastructure`）を、LLM を伴う 3 つの口に掛ける。
- 段は `Bff:UserRateLimits:Mode` で切り替える。段 1（既定）は報告だけ、段 2 は 429。

## 受け入れ基準と試験

- [x] 段 2 では、利用者ごと・群ごとの上限を超えた要求を 429（Retry-After つき）で拒み、別の利用者・別の群は自分の枠を持つ（`BffUserRateLimitTests`。develop の上で 3 件が赤であることを確かめてから実装した）。
- [x] 既定の段では、上限を超えても止めない（同上）。

## 母集合（規則 9・10。着手前に引いた）

- LLM を伴う BFF の口を走査した（`/bff/*` から後段の LLM 用途へ届く経路）。
  - `/bff/analysis/ask`・`/ask/stream`・`/analyze` は rag-answer・analysis に届く。
  - `/bff/search` は、再順位付けが有効なとき rerank に届く。
  - `/bff/graph/suggestions/generate/{id}` は graph-suggestion に届く。
- 除外した口と理由:
  - 図の変換（`/bff/conversion/jobs`）: LLM の呼び出しは変換ジョブの非同期の処理にあり、要求の時点では呼ばない。
  - グラフのクラスタ要約: 利用者の操作では起動しない。
- 「流量制限は無い」の記述を引いた（規則 10）。`GraphBffEndpoints.cs` の 1 か所だけだったので、改めた。

## 対象外

- エッジ（Ingress）でのレート制限。レプリカ間で状態を共有すること（残余に書く）。段 2 への切り替え。
