---
title: 縮退応答の「使用モデル」を実解決値へ是正し、存在しない設定キー Llm:DefaultModel を除去する（issue #403）
type: spec
status: done
related_ids:
  - FR-04
  - FR-05
  - FR-11
  - UC-01
  - UC-02
  - SC-08
  - ADR-0010
  - ADR-0022
  - ADR-0025
  - IADR-0022
  - IADR-0101
  - IADR-0104
  - IADR-0106
  - IADR-0108
author: claude
created: 2026-07-28
updated: 2026-07-28
related_specs:
  - "../adr/IADR-0108_degraded-answer-model-label.md"
  - "../adr/IADR-0106_rag-answer-sonnet-5.md"
  - "../adr/IADR-0101_default-model-opus-5.md"
  - "../adr/IADR-0104_llm-stop-reason-refusal.md"
  - "./20260726_issue-381_rag-answer-sonnet-5.md"
  - "../functional/FR-04_ai-answer-citations.md"
  - "../functional/FR-11_llm-egress-routing.md"
  - "../tests/FR-04_ai-answer-citations.md"
  - "../screens/SC-08_ai-analysis-dashboard.md"
---

# 仕様書: 縮退応答の「使用モデル」是正（issue #403）

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#403](https://github.com/endazon/microservices-platform/issues/403)（`bug` / size S）。
  [[IADR-0106]] §フォローアップ 3 として記録した内容の起票（#381 / PR #402 の副次発見。
  [[IADR-0101]] レビューの 🟢1 も同一箇所を指摘）。
- 要求: **FR-04**（AI 回答と出典。応答契約 `AiAnswerDto`）、**FR-11**（LLM 送信可否の統制。縮退 `Sent=false`）、
  **FR-05**（ABAC・deny-by-default の空回答縮退）、UC-01 / UC-02、**SC-08**（モデル・トークン数の補足表示）。
- 設計: [ADR-0010](../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md)（LLM ゲートウェイ・本文凍結）、
  [ADR-0022](../../planning/projects/microservices-platform/07_adr/ADR-0022_llm-model-sonnet-5.md)（定型 RAG 回答＝Sonnet 5）、
  [ADR-0025](../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md)（既定＝Opus 5）、
  [[IADR-0022]]（用途別ルーティング）、[[IADR-0104]]（`stopReason` による拒否判別）。
- 本作業の実装判断は [[IADR-0108]]。
- **#381 とは別の欠陥**である（版数追随では解消しない）。#381 の追随は PR #402 で完了済み。

## 背景と問題（原因の確定）

`RagOrchestrator`（`src/knowledge/backend/Services/AiAnalysisService/src/AiAnalysisService.Api/Foundation/Services/RagOrchestrator.cs`）
は 3 箇所で「使用モデル」として次の式を用いる。

```csharp
config["Llm:DefaultModel"] ?? "claude-opus-5"
```

| 箇所 | 経路 | LLM 呼び出し |
| --- | --- | --- |
| `AskStreamAsync` L63/89/119 | ABAC で閲覧可能文書なし → `AskDoneEvent.Model`／done で `ev.Model` が空のときの穴埋め | なし |
| `EmptyAnswer()` L375 | 同上（非ストリーミング版）→ `AiAnswerDto.Model` | なし |
| `GenerateAsync` L268/305/332 | ゲートウェイが送信拒否（`Sent=false`）／ゲートウェイ HTTP 失敗時の縮退応答 | なし |

問題は 2 段ある。

1. **参照キーが実在しない。** `LlmGateway.Api/appsettings.json` に実在するのは `Llm:Model` と
   `Llm:Routing:Endpoints[].DefaultModel` であり、`Llm:DefaultModel` は**どこにも定義されていない**
   （リポジトリ全体の grep で、定義側のヒットは 0。参照側 3 箇所とテストの `BuildConfig` 2 箇所のみ）。
   `??` の左辺は常に `null` で、実質ハードコードの `"claude-opus-5"` が返り続ける。設定で差し替える余地もない。
   なお `AiAnalysisService` の `appsettings.json` は `Llm:*` セクション自体を持たない（ゲートウェイ側の設定であり、
   仮にキーを新設しても**サービス境界を越えた設定の二重管理**になる）。
2. **その値が実際の用途別モデルと一致しない。** 本経路の purpose は `rag-answer` で、割当は
   `claude-sonnet-5`（ADR-0022 / [[IADR-0106]]）。`default` 層（Opus 5）の値を名乗るのは、実際に LLM を
   呼んだ場合とも食い違う。

結果として、**一度も LLM を呼んでいない応答が `claude-opus-5` を「使用モデル」として自己申告する**。
この値は応答契約（`AiAnswerDto.Model` / `AskDoneEvent.Model`）に載り、SC-08 の画面表示・監査・
利用状況の集計へ流れる。例外にもログにも現れないため、静かに誤った値が流れ続ける。

### ゲートウェイは既に「使用モデルなし」を表現している（重要）

`CompletionEndpoints` は縮退の各経路で `Model` を次のように埋めている。

| ゲートウェイ側の経路 | `Sent` | `Model` |
| --- | --- | --- |
| 越境拒否（`decision.Allowed=false`。プロバイダ未呼出） | `false` | **`string.Empty`** |
| プロバイダ keyed DI 未登録（未呼出） | `false` | **`string.Empty`** |
| 呼び出し先が不調（例外。**呼び出しは試みた**） | `false` | `decision.Model`（**実 route 結果**） |
| 送信成立（拒否 `refusal` を含む） | `true` | `decision.Model`（実 route 結果） |

つまり**「送信していない＝空文字」「解決した＝route 結果」という表現はゲートウェイ側に既にある**。
`RagOrchestrator` はその値を捨てて（`string.IsNullOrEmpty(ev.Model) ? defaultModel : ev.Model` /
`Sent=false` 分岐では `completion.Model` を見ずに `defaultModel`）ハードコード値で塗り潰している。
本件は「新しい表現を発明する」問題ではなく、**呼び出し側が捏造をやめて透過する**問題である。

## 対象範囲

### 変更する

| 対象 | 変更内容 |
| --- | --- |
| `RagOrchestrator.cs` | `IConfiguration` 依存と `Llm:DefaultModel` 参照 3 箇所を除去。縮退応答の Model は「ゲートウェイ報告値の透過」、ゲートウェイを呼んでいない経路は空文字（`NoModel` 定数）。報告値は `ModelOrNone` で正規化する（JSON の `model` 欠落・`null` を応答契約へ載せない） |
| `RagOrchestratorScopeTests.cs` / `RagOrchestratorStopReasonTests.cs` | コンストラクタ変更に追随（`BuildConfig()` 廃止） |
| `RagOrchestratorDegradedModelTests.cs`（新規） | T-10〜T-15。3 縮退経路＋正常経路の Model を固定 |
| `AnalysisDashboardPage.tsx`（SC-08） | `model` が空のとき「モデル: 未使用（AI へ送信なし）」を表示（空ラベルのぶら下がりを防ぐ） |
| `AnalysisDashboardPage.test.tsx` | 上記の表示を固定（T-15f） |
| `docs/adr/IADR-0108_*`（新規）・`docs/adr/README.md` | 決定の記録と索引 |
| `docs/functional/FR-04` / `FR-11` | 縮退時の `Model` の意味を明記 |
| `docs/tests/FR-04` | T-10〜T-15 を追加 |
| `docs/screens/SC-08` | モデル補足の表示規則（空＝未送信）を明記 |

### 変更しない（意図的に対象外）

- **`AiAnswerDto.Model` / `AskDoneEvent.Model` の型**（`string` のまま。`null` 許容へ変えない）。
  理由は [[IADR-0108]] §検討した選択肢。フロントの TS 型（`model: string`）も無変更。
- **`Llm:DefaultModel` の新設**（実在キーへの是正案）。存在しないキーを定義して延命しない。
- **`LlmGateway` 側の挙動**（`CompletionEndpoints` の `Model` の埋め方・ルーティング・`PurposeModels`）。
  本件は呼び出し側の欠陥であり、ゲートウェイは既に正しい値を返している。
- **`SearchChatPage`（SC-01）**。`done` の `model` を受け取るが**画面に表示していない**（`answerId` のみ使用）。
  表示追随は不要（テストのフィクスチャ `model: 'm'` も無変更で通る）。
- 同一サービスの #394 / #395 / #379 の論点（別 issue・ファイル競合回避のため）。
- SIMULATE / 実弾スイッチに類する設定（本作業は触れない）。

## 決定（要約。詳細は [[IADR-0108]]）

**縮退応答は「使用モデル」を捏造せず、ゲートウェイが報告した値をそのまま透過する。ゲートウェイを
呼んでいない／ゲートウェイに到達できない経路は空文字（＝モデル未使用）を返す。**

| RagOrchestrator の経路 | LLM | 返す `Model` |
| --- | --- | --- |
| ABAC 不許可（`EmptyAnswer` / `AskStreamAsync` 権限なし分岐） | 呼ばない（ゲートウェイも呼ばない） | `""` |
| ゲートウェイ HTTP 非 2xx・通信失敗（`GenerateAsync` 末尾／`StreamCompletionAsync` の送信失敗） | 到達せず | `""` |
| ゲートウェイが越境拒否（`Sent=false`・`Model=""`） | 呼ばない | `""`（ゲートウェイ値を透過） |
| ゲートウェイが呼び出し先不調（`Sent=false`・`Model=route 結果`） | 試みて失敗 | route 結果（透過） |
| 送信成立（`refusal` 含む） | 呼んだ | route 結果（透過。従来どおり） |

## 実装方針（TDD）

1. **Red**: `RagOrchestratorDegradedModelTests`（新規・10 ケース）で 3 縮退経路と正常経路の `Model` を固定する。
   実行結果は **7 失敗 / 3 成功**で、失敗はすべて「空を期待したが `claude-opus-5` が返った」（＝欠陥の再現）。
   成功した 3 件は既に透過が効いていた経路（送信成立 2 件・SSE の呼び出し先不調 1 件）。
2. **Green**: `RagOrchestrator` から `IConfiguration` と `Llm:DefaultModel` 参照を除去し、透過へ変更する。
3. **追随**: 既存 2 テストのコンストラクタ、フロント SC-08 表示とそのテスト、仕様書・IADR・索引。
4. **検証**: `dotnet build` / `dotnet test`（platform・knowledge 両ユニット）/ `dotnet format --verify-no-changes`、
   `npm run lint` / `typecheck` / `test`（src/）。

## テスト観点

| ID | 観点 | 期待 |
| --- | --- | --- |
| T-10 | `AskAsync` ABAC 不許可（LLM 未呼出） | `AiAnswerDto.Model == ""`（`claude-opus-5` でない） |
| T-11 | `AskStreamAsync` ABAC 不許可 | `AskDoneEvent.Model == ""` |
| T-12 | `AskAsync` ゲートウェイ越境拒否（`sent=false`・`model=""`） | `Model == ""` |
| T-13 | `AskAsync` ゲートウェイ HTTP 失敗（非 2xx） | `Model == ""` |
| T-14 | `AskAsync` / `AskStreamAsync` 正常（`sent=true`・`model=claude-sonnet-5`） | 実モデル名を透過（回帰防止） |
| T-15 | `AskAsync` / `AskStreamAsync` 呼び出し先不調（`sent=false`・`model=claude-sonnet-5`） | route 結果を透過（空へ潰さない） |
| T-15f | SC-08 表示 | `model` 空なら「モデル: 未使用（AI へ送信なし）」、非空なら従来どおりモデル名 |

## 受け入れ基準（issue #403 §受け入れ基準に対応）

- [x] 存在しない設定キー `Llm:DefaultModel` への参照が `RagOrchestrator` から除去されている
- [x] LLM を呼び出していない縮退応答が返す「使用モデル」の方針が決定され、実装 ADR（[[IADR-0108]]）に記録されている
- [x] 3 経路（ABAC 不許可 / 送信拒否 / 呼び出し失敗）それぞれについて、決定した値が返ることをテストで固定した
- [x] 実際に LLM を呼び出した場合は、従来どおり実モデル名が返ることが回帰していない
- [x] 応答契約（`AiAnswerDto` / `AskDoneEvent`）の変更有無と、フロント表示・既存テスト固定値への影響が確認・追随されている
- [x] 機能仕様書（FR-04 / FR-11）・テスト仕様書に追随した

## 完了条件（DoD）

- `dotnet build` / `dotnet test` が platform・knowledge 両ユニットで通る
- `dotnet format --verify-no-changes` が両ユニットで通る
- `npm run lint` / `npm run typecheck` / `npm run test` が `src/` で通る
- 上表の受け入れ基準がすべてチェック済み
- `docs/DEFINITION_OF_DONE.md` を満たす
