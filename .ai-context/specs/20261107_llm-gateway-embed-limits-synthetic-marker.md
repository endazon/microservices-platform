---
title: 作業仕様書 — LLM ゲートウェイの埋め込みの面に呼び手の許可表と送る前の上限を掛け、合成監視の標識を引き継ぐ呼び手を許可集合に限る（どちらも段つき）
type: spec
status: done
related_ids: [NFR-18, NFR-21, NFR-02, FR-02, FR-11, ADR-0044, ADR-0076, IADR-0678, IADR-0684, IADR-0692, IADR-0698, IADR-0378, IADR-0702]
author: claude
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# 作業仕様書 — LLM ゲートウェイの埋め込みの面の統制と、合成監視の標識の引き継ぎ手の限定

> 本仕様書は、実装の前に作成した（着手 2026-11-07。develop `cc8f316f1`）。決定は IADR-0702（仮の番号。組 E の IADR-0701 との衝突で 0701 から改番）。

## 起点となる計画書（トレーサビリティ）

- 非機能要件: NFR-18（統制）・NFR-21（費用と運用の観測）・NFR-02（合成監視）。機能要求: FR-02（埋め込み）・FR-11（LLM 費用の計測）。
  計画 ADR-0044（費用の計測）・ADR-0076 決定 4（合成の標識と費用の計上からの除外）。
- 画面（SC）・ユースケース（UC）: なし。
- 関連 IADR: IADR-0678（送る前の上限）・IADR-0684（補完の呼び手ごとの許可表）・IADR-0692（クラウドの当日の上限 合計 20 USD）・
  IADR-0698（当日の費用の枠の置き場）・IADR-0378（合成の標識）。
- 所有者の裁定: 当日の費用の上限は全呼び手の合計で 1 日 20 USD（2026-11-07）。

## 現状の確認（変更の前に実測。develop `cc8f316f1`）

- 埋め込み（gRPC `LlmEmbedding/Embed`。`Features/Embeddings/Embed/GrpcService.cs`・`EmbedUseCase.cs`）は、門（`ServiceCaller`）だけを通り、
  呼び手ごとの許可表（`LlmCallerPolicy`）も送る前の上限（`LlmCallLimiter`）も参照しない（`git grep -n "LlmCallerPolicy\|LlmCallLimiter" -- '*Embed*'` が 0 件）。
- クラウドの描画（`deploy/cloud/values-cloud.yaml`）は `embedding.enabled: false`・`grpcPort: null` だが、ゲートウェイの受け口（TLS の 8080）は HTTP/2 も受けるので
  gRPC の面は在る。埋め込みの鍵（`voyage-api-key`）は手順書 `docs/operations/cloud-openbao-init-unseal-runbook.md` §4 の表が「埋め込みを使うなら」と案内している。
- 補完の両面（REST `/complete`・gRPC `Complete`/`CompleteStream`）は、合成監視の標識（ヘッダ）が在れば、呼び手が誰でも合成として扱い、費用の計上から外す
  （`SyntheticTraffic.IsSyntheticInternalRequest`）。標識をゲートウェイへ運ぶ正規の呼び手は `aianalysis-service`・`retrieval-service` の 2 つだけ
  （`git grep -n "SyntheticTraffic.PropagateTo" -- 'src/*/backend/Services'`）。取引ユニットは標識を使わない（AST の木を走査して 0 件）。
- クラウドは補完の許可表・上限とも段 2（`Enforce`）。既定とローカルの描画は段 1（`Report`）。

## 決定の要旨（詳細は IADR-0702）

1. 埋め込みの面も、補完と同じ 1 つの許可表と 1 つの上限の判定器を通す（判定器を 2 つにしない）。
   - 許可表: 呼び手の項目に `AllowEmbedding`（既定 false）。段は `Llm:CallerPolicy:EmbeddingMode`（Off / Report / Enforce。既定 Report）。
   - 上限: 呼び手の要求数・当日の費用（補完と同じ呼び手の枠に合算する）・入力の長さ。用途の枠は固定の `embedding`。
     段は `Llm:CallLimits:EmbeddingMode`（Off / Report / Enforce。既定 Report）。外へ出ない経路（ティア A）は費用を数えない。
   - 費用の見込み・実額は単価表（`Llm:Pricing:Models`）で換算する。`voyage-3.5` の単価を足す（計画の比較時点の目安。正は契約の価格表）。

     ［2026-11-07 追記 / #2506］上の 1 行は実装と食い違う。埋め込みの単価は補完の単価表（`Llm:Pricing:Models`）に足さず、
     別の表 `Llm:CallLimits:EmbeddingPrices`（既定 `voyage-3.5`）に置いた。補完の単価表は計画 ADR-0138 決定 7 が 3 モデルに限り、
     既存の試験がそれを固定しているため。表に無いモデルは 0 にせず `UnknownModelPrice` の入力単価で数える（IADR-0702）。
2. 合成監視の標識を引き継ぐ呼び手を許可集合 `Llm:SyntheticMarker:TrustedCallers` に限る。段は `Llm:SyntheticMarker:Mode`
   （Report＝従来どおり引き継ぎ、許可集合の外を数えて報告／Enforce＝許可集合の外の標識を無視して費用に計上する）。既定 Report。
3. クラウドは 3 つとも段 2（`Enforce`）。取引ユニットの呼び手には埋め込みを許可しない。クラウドの手順書から埋め込みの鍵の案内を外す。

## 受け入れ基準と試験の写像

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC1 | 埋め込みの許可表: 段 2 で許可の無い呼び手（表に無い・`AllowEmbedding` が偽）を拒み、段 1 は通して数え、Off は判定しない | `LlmCallerPolicyTests.埋め込みは許可の無い呼び手を段2で拒み段1で通し_Offは判定しない`・`埋め込みの許可がある呼び手は違反なし` |
| AC2 | gRPC 面: 段 2 で許可の無い呼び手は `PERMISSION_DENIED`・プロバイダへ届かない。許可のある呼び手は従来どおり埋め込む | `EmbedGuardEndpointTests.埋め込みの許可が無い呼び手は段2でPERMISSION_DENIED_プロバイダへ届かない`・`既定の段では許可の無い呼び手も埋め込む`（陽性対照） |
| AC3 | 埋め込みの上限: 段 2 で呼び手の要求数・当日の費用・入力の長さを超えたら送らず `Embedded=false`（長さは再試行しない・ほかは再試行する）。段 1 は送る | `LlmCallLimiterEmbeddingTests` の各件・`EmbedGuardEndpointTests.段2では埋め込みの要求数の上限を超えたら送らない` |
| AC4 | 埋め込みの費用は補完と同じ呼び手の当日の枠に合算する（20 USD の合計が埋め込みで抜けない） | `LlmCallLimiterEmbeddingTests.埋め込みの費用は補完と同じ呼び手の当日の枠に合算する` |
| AC5 | 外へ出ない経路（ティア A）の埋め込みは費用を数えない（要求数は数える） | `LlmCallLimiterEmbeddingTests.外へ出ない経路の埋め込みは費用を数えず要求数だけ数える` |
| AC6 | 段 2 の埋め込みも、既定の当日の費用の上限が無いと起動しない。用途の費用の枠のキーに `embedding` を書ける | `LlmCallLimitOptionsValidatorTests` の追加 2 件 |
| AC7 | 合成の標識: 許可集合の呼び手は引き継ぐ。外の呼び手は、段 1 で引き継いで数え、段 2 で無視して費用に計上する。標識が無ければ合成でない | `LlmSyntheticMarkerPolicyTests` の各件・`LlmSyntheticUsageExclusionTests` の追加（REST・gRPC。費用の系列に載る／載らない） |
| AC8 | クラウドの描画は 3 つの段 2（埋め込みの許可表・埋め込みの上限・標識）を持ち、既定とローカルの描画は持たない | `scripts/deploy-cloud.test.js` IADR-0689 決定 12 の試験（`STAGE2_LLM_ENV` に 3 つを足す。陰性対照は既存の extraEnvAppend=null） |
| AC9 | 既定の設定: 埋め込みの許可は取り込み・検索の 2 呼び手だけ、取引ユニットの呼び手には無い。標識の許可集合は AI 分析・検索の 2 呼び手だけ | `LlmCallerPolicyTests.既定の設定では埋め込みの許可は取り込みと検索だけ_標識の許可集合はAI分析と検索だけ` |
| AC10 | クラウドの手順書は埋め込みの鍵を案内しない | `scripts/deploy-cloud.test.js` の追加 1 件（陰性対照つき） |

試験は修正を外すと赤になることを、各件で確かめる（陰性対照）。

## 是正の母集合（規則 9・10）

誤りの側の文字列で走査した（`git grep -n "voyage-api-key\|埋め込みを使うなら\|標識は外周（BFF）が\|外周が付けたヘッダを引き継ぐ\|7 値で閉じる\|3 値で閉じる"`）。

| 場所 | 扱い |
| --- | --- |
| `docs/operations/cloud-openbao-init-unseal-runbook.md` §4 の表 | 埋め込みの鍵の案内を外す（クラウドでは埋め込みを使わない） |
| `deploy/cloud/vault/eso/microservices-platform.yaml` の注記 | 「voyage-api-key は任意」を外す |
| `deploy/cloud/vault/openbao-scripts.test.sh`（put の試験のキー名） | 対象外（書き込みの道具の試験。キーの意味に依らない） |
| `docs/observability/llm-usage-and-cost-metrics.md` §送る前の上限・§監視観点 | 埋め込みの段・用途 `embedding`・標識の許可集合を足す |
| `docs/observability/synthetic-traffic-exclusion.md` の内周の行 | 内周で標識を引き継ぐ呼び手を許可集合に限ることを足す |
| `docs/api/east-west-grpc.md` §2 つ目の面（埋め込み） | 許可表と上限の判定を足す |
| `Features/Completions/Complete/Endpoint.cs` の「外周が付けたヘッダであり、ここでは引き継ぐだけ」 | 許可集合の判定を経ることに直す |
| `LlmCallerPolicy` の「3 値で閉じる」・`LlmCallLimiter` の種類の注記 | 違反の種類は 4 値へ。上限の種類（7 値）は変えない（埋め込みは既存の種類を使う） |
| IADR-0378・IADR-0684 の本文 | 日付つき追記（内周の受容の前提・埋め込みの面） |
| `.ai-context/specs/` の既存の仕様書 | 凍結記録なので直さない |

## 実装の範囲

- `Domain/Authorization/LlmCallerPolicy.cs`（`AllowEmbedding`・`EmbeddingMode`・`EvaluateEmbedding`）・`Domain/Authorization/LlmSyntheticMarkerPolicy.cs`（新規）。
- `Domain/Limits/LlmCallLimitOptions.cs`・`LlmCallLimitOptionsValidator.cs`・`LlmCallLimiter.cs`（`AdmitEmbedding`）。
- `Features/Embeddings/Embed/GrpcService.cs`・`EmbedUseCase.cs`・`Features/Completions/Complete/Endpoint.cs`・`Features/Completions/GrpcService.cs`・`Program.cs`・`appsettings.json`。
- `deploy/cloud/values-cloud.yaml`・`scripts/deploy-cloud.test.js`・手順書と可観測性の文書（上の表）。

## 残るもの

- 埋め込みの実額は提供元の応答のトークン数ではなく、文字数を入力トークンとみなした見込み額で置き換える（多めの側）。
- 要求数の窓はプロセス内（IADR-0678 決定 5 と同じ）。
