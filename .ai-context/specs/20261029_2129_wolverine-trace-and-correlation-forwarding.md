---
title: 作業仕様書 — Wolverine のメッセージ処理をトレースに繋ぎ、相関 ID をトレース ID に揃えて後段へ引き継ぐ（#2129）
type: spec
status: done
related_ids: [NFR-19, NFR-28, ADR-0006, ADR-0027, IADR-0216, IADR-0244, IADR-0649]
author: claude
created: 2026-10-29
updated: 2026-10-29
issue: "#2129"
---

# 作業仕様書 — トレースと相関の連続（#2129）

> 本仕様書は実装着手前に作成した（着手 2026-10-29。develop `dc720cd6`）。親は #2047（指摘 M-26）。

## 現状（実測）

- `ObservabilityExtensions.AddPlatformObservability` の `WithTracing` は AspNetCore と HttpClient の計装だけを持ち、Wolverine の ActivitySource（名前 `Wolverine`。`Wolverine.Runtime.WolverineTracing`）を聞いていない。
  - 聞き手が居ないと Wolverine は受信側の Activity を作らない（`StartActivity` が null）。エンベロープが運ぶ親は使われない。
  - 実測（下の試験・修正前）: HTTP の要求からローカルキューへ発行したメッセージのハンドラで `Activity.Current` が null。
- `CorrelationIdMiddleware` は、ヘッダ `X-Correlation-ID` が無ければ新しい GUID を作る。受けた値を後段へ転送する仕組みも無い（`DelegatingHandler` 0 件）。
  - BFF → 後段の 1 ホップで相関 ID が別物になる。
  - 受けた値をそのままログのスコープへ載せていた（形の検査なし。NFR-28 の観点で外由来の文字列）。

## 方針（判断は IADR-0649）

1. `WithTracing` に `AddSource("Wolverine")` を足す。全サービスが同じ拡張を使うので 1 か所で効く。
   - 名前は Wolverine の型が internal のため写す。写し間違えは、試験が Wolverine の実物（リフレクション）と突き合わせて捕まえる。
2. **相関はトレース ID に揃える。** ヘッダが無い・形が悪いときは、現在のトレース ID を相関 ID にする。
   - 後段は traceparent から同じトレース ID を導けるので、ヘッダが届かない経路（メッセージなど）でも一致する。
3. 受けた要求の相関 ID を、`IHttpClientFactory` のすべてのクライアントで後段へ引き継ぐ（`ConfigureHttpClientDefaults` に `CorrelationIdForwardingHandler`）。
   - 呼び出し側が付けた値は上書きしない。要求の外では付けない。
4. 受けた値は英数字と `._:-` の 128 文字までに限る。外れたらトレース ID にする（NFR-28。ログのスコープと後段への転送に載るため）。

## 受け入れ基準

- [x] HTTP → メッセージ → コンシューマが同じトレースに載る（ローカルキューで実ホストを起こす試験。修正前は赤＝ハンドラの Activity が null）
- [x] トレースが Wolverine の ActivitySource を聞いている。名前は Wolverine の実物と一致する
- [x] BFF から後段（認可サービス）への呼び出しで相関 ID が引き継がれ、応答の値とも一致する（修正前は赤）
- [x] ヘッダが無ければ相関 ID はトレース ID。形の悪い値・長すぎる値は採らない
- [x] 呼び出し側が付けた値を上書きしない。要求の外では付けない

## 母集合（規則 9）

- `git grep -n "AddPlatformObservability"` → 本番 15 サービス（BFF 含む）がすべてこの拡張を使う。個別の `WithTracing` を持つサービスは無い（`AddSource` を個別に足している箇所 0）。
- `git grep -n "X-Correlation-ID"` → `CorrelationIdMiddleware` だけ。転送の仕組み（`DelegatingHandler`）は 0 件。
- `git grep -n "相関 ID" -- docs` → 運用・構成の文書の言及は「相関 ID がある」までで、転送・決め方を書いた文書は無い。追随は不要。
- 規則 10: `CorrelationIdMiddleware` の冒頭コメントを直す（値の決め方が変わる）。

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。
