---
title: IADR-0649 Wolverine の ActivitySource をトレースに加え、相関 ID はトレース ID に揃えて IHttpClientFactory の全クライアントで後段へ引き継ぐ
type: impl-adr
status: Accepted
related_ids: [NFR-19, NFR-28, ADR-0006, ADR-0027, IADR-0216, IADR-0244]
author: claude
created: 2026-10-29
updated: 2026-10-29
related_specs:
  - ../specs/20261029_2129_wolverine-trace-and-correlation-forwarding.md
---

# IADR-0649: トレースと相関の連続（#2129）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-29
- 決定者: claude（#2129。親 #2047 の指摘 M-26）

## 起点・関連

- 起点 issue: #2129。計画 ADR-0006「相関 ID でログ・トレース・メトリクスを突合する」。
- 前提として変えない: [IADR-0216](./IADR-0216_otel-logging-sdk-replaces-serilog.md)（ログは OTel Logs。スコープを LogRecord の属性へ載せる）。
- 作業仕様書: [20261029_2129_wolverine-trace-and-correlation-forwarding](../specs/20261029_2129_wolverine-trace-and-correlation-forwarding.md)。

## 背景

- トレースは Wolverine の ActivitySource を聞いておらず、メッセージの受信側でスパンが作られなかった。コンシューマのログとスパンは、発行元の HTTP 要求のトレースから切れていた。
- 相関 ID は毎ホップで新しい GUID になり、後段へ転送されなかった。

## 決定

### 決定 1: 共通の拡張で Wolverine の ActivitySource を聞く

- `AddPlatformObservability` の `WithTracing` に `AddSource("Wolverine")` を足す。全サービスがこの拡張を使うので 1 か所で効く。
- 名前は写す（`WolverineTracing` は internal）。写し間違えは、試験が Wolverine の実物と突き合わせて捕まえる。

### 決定 2: 相関はトレース ID に揃える

- ヘッダ `X-Correlation-ID` が無い・形が悪いときは、現在のトレース ID を相関 ID にする（従前は GUID）。
- 後段は traceparent から同じトレース ID を導ける。ヘッダが届かない経路（メッセージ・要求の外の呼び出し）でも、相関 ID とトレース ID が一致する。
- **採らなかった案: 相関 ID をメッセージのヘッダにも載せる。** トレース ID に揃えれば、メッセージの側はトレースの連続（決定 1）だけで足りる。持ち回る値を 2 つにしない。

### 決定 3: 受けた値は IHttpClientFactory の全クライアントで後段へ引き継ぐ

- `ConfigureHttpClientDefaults` に `CorrelationIdForwardingHandler` を積む。gRPC のクライアントも同じ工場から作られるので、east-west の gRPC にも効く。
- 呼び出し側が自分で付けた値は上書きしない。要求の外（`HttpContext` が無い）では付けない。

### 決定 4: 受けた値の形を限る（NFR-28）

- 英数字と `._:-` の 128 文字までに限る。外れたらトレース ID にする。値はログのスコープと後段への転送に載るので、行構造を壊せる値を採らない。

## 残余リスク

- 外部の宛先（LLM の提供元・Keycloak・外部データソース等）への呼び出しにも、要求の中なら `X-Correlation-ID` が付く。**受容する。** 理由:
  - 既定の伝播（.NET の DiagnosticsHandler と HttpClient の計装）が、外向きの要求に traceparent を既に書いている。相関 ID は既定ではそのトレース ID と同じ値で、新しく出る情報は無い（2026-10-29 に実ソケット越しで実測。`CorrelationIdTests` の「外向きの呼び出しには既定で traceparent が載り相関 ID はそのトレース ID と同じ」が固定する）。
  - 呼び出し側が持ち込んだ値は `CorrelationId.Resolve` を通る（英数字と `._:-` の 128 文字まで。決定 4）。転送するのは Resolve 後の値だけで、受けた文字列がそのまま外へ出る経路は無い。
  - 本番コードに伝播を止める設定は無い（`DistributedContextPropagator`・`EnableActivityPropagation` の上書き 0 件）。止める設定を足すときは、本項を見直す（そのときは相関 ID だけが外へ出ることになる）。
- 利用者が相関 ID を送ってきた場合、その値はトレース ID と一致しない。ログでは両方（スコープの `CorrelationId` と TraceId）が並ぶので突合はできる。
- Wolverine の ActivitySource の名前が版上げで変わると、聞き手が空振りする。名前の突き合わせの試験が版上げの PR で赤になる。
