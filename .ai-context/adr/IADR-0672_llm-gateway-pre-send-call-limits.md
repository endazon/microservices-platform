---
title: IADR-0672 LLM ゲートウェイは送る前に、入力の上限と、呼び手ごと・呼び手×用途ごとの要求数と当日の費用（見込み額を含む）を 1 か所で判定する。段 1 は報告だけ、段 2 は設定の切り替えで Sent=false にする（#2404）
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-21, FR-10, FR-11, ADR-0010, ADR-0044, IADR-0378, IADR-0400, IADR-0424, IADR-0466]
author: claude
created: 2026-11-05
updated: 2026-11-05
related_specs:
  - ../specs/20261105_2404_llm-gateway-call-limits.md
---

# IADR-0672: LLM ゲートウェイの送る前の呼び出しの上限（#2404）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: claude（#2404。コーディネーターの指示 2026-11-05）

## 起点・関連

- 起点 issue: #2404。
- 前提:
  - 計画 ADR-0044（LLM 費用の計測。換算はゲートウェイ）・ADR-0010（呼び出し先の切り替え）。
  - IADR-0466（用途別の月次予算のゲージ。金額の既定を置かない）。
  - IADR-0424（補完の 2 つの面は同じ `ServiceCaller` で判定する）・IADR-0400（判定器を 2 つにしない・縮退は例外にしない）。
  - IADR-0378（合成監視の標識）。
- 作業仕様書: `.ai-context/specs/20261105_2404_llm-gateway-call-limits.md`。

## コンテキスト

- 費用の統制は、月次予算のゲージと上限アラート（送った後の観測）だけだった。送る前に止める判定が無かった。
- `max_tokens` とプロンプトの長さに上限が無かった。
- `ServiceCaller` はロールを見る門であり、呼び手ごとの量を区別しない。
- llmgateway は 1 レプリカで動いている。

## 決定

### 決定 1 — 判定は `LlmCallLimiter` の 1 か所に置き、`CompletionUseCase` の一括・逐次の両方が送る直前に呼ぶ

- 越境判定とプロバイダの解決の**後**、最初の送信の**前**に呼ぶ（見込み額の換算にモデルが要るため）。
- 輸送（REST・gRPC）は呼び手の名前を渡すだけで、判定を持たない。

### 決定 2 — 判定する上限

| 種類（`llm.limit.kind`） | 対象 | 既定 |
| --- | --- | --- |
| `max_tokens` | `max_tokens` が 1 以上かつ `MaxOutputTokens` 以下 | 32768 |
| `prompt_length` | `CacheablePrefix` ＋ `Prompt` の文字数が `MaxPromptChars` 以下 | 800000 |
| `caller_rate` | 呼び手の直近 60 秒の件数（全用途の合計）が `RequestsPerMinute` 未満 | 300 |
| `purpose_rate` | 呼び手 × 用途の直近 60 秒の件数が `PurposeRequestsPerMinute` 未満 | 120 |
| `caller_cost` | 呼び手の当日（UTC）の費用 ＋ 見込み額が `DailyCostLimit` 以下 | なし（所有者が設定） |
| `purpose_cost` | 呼び手 × 用途の当日の費用 ＋ 見込み額が `PurposeDailyCostLimits[用途]` 以下 | なし（所有者が設定） |

- 既定値は、既知の呼び手の最大（`max_tokens` は 16384）より上に置いた。段 1 は報告だけなので、既定値は「報告の閾値」として働く。
- 呼び手ごとの値は `Llm:CallLimits:Callers:<呼び手>` で上書きする。**書かなかった項目は `Default` の値を使う**（呼び手の項目を足したことで、他の上限が黙って外れない）。
- 見込み額 = 単価表で（入力 = プロンプトの文字数、出力 = `max_tokens`）を換算した額。送った後は実額（応答のトークン数・キャッシュの書き・読み）へ置き換え、送らなかったとき（越境拒否は判定の前なので対象外。プロバイダ未登録・上流の失敗・取り消し）は見込みを戻す。
- 費用の上限が 1 つも設定されていない呼び手では、換算をしない（単価表の警告を増やさない）。

### 決定 3 — 呼び手は検証済みのトークンの主体、用途は既知の用途へ正規化した値で数える

- 呼び手は `MachinePrincipal.ClientIdOf`（`azp` 等）。取れなければ `unknown` の 1 つの枠。
- 用途は計器と同じ正規化（`LlmMetricValues.NormalizePurpose`。既知の用途・`default`・それ以外は `other`）を通す。
- 合成監視の標識が付いた呼び出しも数える。費用の計上から外すこと（IADR-0378）と、上限から外すことは別の判断であり、後者はしない。

### 決定 4 — 段は `Llm:CallLimits:Mode`。既定は `Report`

- `Report`（段 1）: 判定し、計器 `llm.call_limit.exceeded.total` と Warning ログ（同じ種類・呼び手・用途は 5 分に 1 行）を出す。送信は止めない。
- `Enforce`（段 2）: 超えた呼び出しは送らず、`Sent=false`・`FailureKind=limit_exceeded`（共有契約に足す）で返す。逐次は `done=true, sent=false` の 1 イベント。`llm.completion.total` にも `llm.result=limit_exceeded` で数える。
- 段 2 では、拒否した呼び出しは要求数・費用の枠を消費しない。

### 決定 5 — 状態はプロセス内に持つ

- 要求数は鍵ごとの時刻の列（60 秒の窓）、費用は鍵ごとの（UTC の日付・合計）で持つ。鍵の数は「呼び手（realm の有限集合）×正規化した用途」で閉じる。

## 結果・残余リスク

1. **状態はレプリカごとである。** レプリカを増やすと、実効の上限はレプリカ数倍になる。増やす前に共有の置き場（Valkey 等）へ移す判断が要る。
2. 再起動で当日の費用の枠は 0 に戻る（事後の観測である月次予算のアラートは引き続き働く）。
3. 見込み額は文字数を入力トークンの見込みに使う。英文では過大、日本語ではおおむね近い。過大な見込みは送った後に実額へ置き換わるので、枠を一時的に多く使うだけである。
4. 埋め込み（`EmbedUseCase`）は本 IADR の射程外。
5. 呼び手 × 用途の許可表（どの呼び手がどの用途を呼べるか）は持たない。これは認可の変更であり、別に扱う。

## フォローアップ

- 段 2 への切り替えは、報告（`llm_call_limit_exceeded_total`）を所有者が確かめてから設定で行う。
