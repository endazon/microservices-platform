---
title: 作業仕様書 — LLM ゲートウェイの呼び出しの上限を、送る前に呼び手ごとに判定する（段 1 は報告・強制は設定の切り替え。#2404）
type: spec
status: done
related_ids: [NFR-18, NFR-21, FR-10, FR-11, ADR-0010, ADR-0044, IADR-0378, IADR-0424, IADR-0466, IADR-0678]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#2404"
---

# 作業仕様書 — LLM ゲートウェイの呼び出しの上限（#2404）

> 本仕様書は、実装に着手する前に作成した（着手 2026-11-05。`origin/develop` `b05b19b9b`）。

## 事象（現物で確かめたこと）

- `CompletionUseCase.ExecuteAsync` / `StreamAsync` は、越境判定（`router.Route`）→ プロバイダの解決 → `provider.CompleteAsync` / `StreamAsync` の順に進む。**その間に上限の判定が無い**。
- 費用の統制は、月次予算のゲージ（`LlmBudgetMetrics`）と Prometheus の上限アラートだけである。**どちらも送った後の観測**であり、送る前には止めない。
- `max_tokens` は既定値（4096）だけを持ち、上限を持たない。gRPC は負数だけを拒み、REST は検証しない。
- プロンプト（`Prompt` と `CacheablePrefix`）の長さは検査しない。輸送の受信上限の既定値だけが効く。
- 門（`ServiceCaller`）は realm ロール `platform-service` を見る。**呼び手（サービスアカウント）ごとの量の区別は無い**。
- llmgateway のレプリカは 1（`deploy/helm/microservices-platform/values.yaml` の `llmgateway.replicas`）。

## 方針（IADR-0678）

1. **判定は 1 か所**（`LlmCallLimiter`）に置き、一括と逐次の両方が `CompletionUseCase` の中で同じ判定を通る（判定器を 2 つにしない。IADR-0400 決定 2 と同型）。
2. 判定する上限（送る前に）:
   - 入力: `max_tokens` が 1 以上かつ `MaxOutputTokens` 以下。プロンプトの長さ（`CacheablePrefix` ＋ `Prompt` の文字数）が `MaxPromptChars` 以下。
   - 要求数: 呼び手ごと（全用途の合計）と、呼び手 × 用途ごとの直近 60 秒の件数。
   - 費用: 呼び手ごと（全用途の合計）と、呼び手 × 用途ごとの UTC の当日の費用。**その呼び出しの見込み額**（プロンプトの文字数を入力トークンの見込み、`max_tokens` を出力の見込みとして単価表で換算）を足して判定する。送った後は見込みを実額へ置き換え、送らなかったときは見込みを戻す。
3. 呼び手は**検証済みのトークンの主体**（`azp` 等。`MachinePrincipal.ClientIdOf`）から決める。用途は**既知の用途へ正規化**してから数える（未知の用途は `other`）。用途の名前を変えても別の枠にならない。
4. 合成監視の標識が付いた呼び出しも上限には数える（費用の計上から外すことと、上限から外すことは別である）。
5. **段**（`Llm:CallLimits:Mode`）:
   - `Report`（既定・段 1）: 判定して計器とログへ出すだけで、送信は止めない。
   - `Enforce`（段 2）: 超えた呼び出しを送らず、`Sent=false`・`FailureKind=limit_exceeded` で返す（例外にしない。IADR-0400 決定 5 と同じ）。
6. 費用の上限の**金額の既定は置かない**（計画が月次予算の金額を定めない方針と同じ。所有者が設定する）。要求数と入力の上限は技術的な既定値を置く（既知の呼び手の最大より上）。
7. 計器 `llm.call_limit.exceeded.total`（属性: 種類・段・呼び手・用途）を同じ Meter に足す。ログは同じ（種類・呼び手・用途）を 5 分に 1 行へ抑える（`LogOccurrenceThrottle`）。

## 受け入れ基準と試験

- [x] `Report` では、上限を超えた呼び出しも送られ、`llm.call_limit.exceeded.total`（`llm.limit.mode=report`）が 1 増える（入力・要求数・費用の各種類）。
- [x] `Enforce` では、上限を超えた呼び出しはプロバイダへ届かず、`Sent=false`・`FailureKind=limit_exceeded` で返る（一括と逐次の両方）。
- [x] 呼び手が違えば枠は別。用途の名前を未知の文字列へ変えても、`other` の 1 つの枠へ数えられる。
- [x] 費用の上限は、当日の実額 ＋ その呼び出しの見込み額で判定する。送った後は実額で置き換わる。送らなかった呼び出し（越境拒否・上流の失敗）は見込みを戻す。
- [x] 合成監視の標識が付いた呼び出しも数える。
- [x] UTC の日付が変わると費用の枠は 0 に戻る。60 秒経つと要求数の枠は空く。
- [x] 不正な設定（0 以下の上限・未知の用途のキー）は起動時に落とす。
- [x] 既定の構成（`appsettings.json`）では `Mode=Report` で、既存の試験はすべて緑のまま（送信の挙動は変わらない）。

試験: `Tests/Domain/Limits/LlmCallLimiterTests.cs`（判定器の単体）、`Tests/Domain/Limits/LlmCallLimitOptionsValidatorTests.cs`、`Tests/Features/Completions/CallLimitEndpointTests.cs`（REST・gRPC の面を通す）。

## 母集合（規則 9・10。着手前に引いた）

- 送信の経路: `grep -rn "provider.CompleteAsync\|provider\s*\.StreamAsync\|\.StreamAsync(new CompletionRequest" src/platform/backend/Services/LlmGateway --include=*.cs` → `CompletionUseCase.cs` の 2 か所だけ（一括・逐次）。判定はこの 2 か所の直前に置く。
- `CompletionUseCase` の生成: DI と、試験の直接生成 2 か所（`SentFalseObservabilityTests.cs`・`StreamUsageOnAbortTests.cs`）。両方を追随する。
- `CompletionFailureKinds` の値域: 共有契約（`CompletionDto.cs`）・計器（`LlmCompletionMetrics`）・契約のベースライン（`scripts/contract-schema-baseline.json`）。3 つを揃える。
- 呼び手が読む `FailureKind`: MSP の呼び手（AiAnalysis・Graph・Retrieval・Conversion）と AST は、未知の値を「原因不明の `Sent=false`」として扱う（共有契約の注記どおり）。段 1 では値が出ないので影響しない。
- 除外: 埋め込み（`EmbedUseCase`）は本 issue の射程外（費用の単位も経路も別。残余に記録する）。

## 対象外

- 呼び手 × 用途の許可表（認可の変更）。BFF での利用者単位の上限。
- 複数レプリカでの状態の共有（現在 1 レプリカ。残余に記録する）。
- 段 2 への切り替え（稼働の設定の変更。所有者が報告を見てから行う）。
