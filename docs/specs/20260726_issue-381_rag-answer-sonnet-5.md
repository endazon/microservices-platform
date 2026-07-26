---
title: rag-answer 用途のモデルを claude-sonnet-5 へ追随する（issue #381）
type: spec
status: done
related_ids:
  - FR-04
  - FR-11
  - UC-01
  - ADR-0010
  - ADR-0022
  - ADR-0025
  - IADR-0022
  - IADR-0101
  - IADR-0102
  - IADR-0106
author: claude
created: 2026-07-26
updated: 2026-07-26
related_specs:
  - "../adr/IADR-0106_rag-answer-sonnet-5.md"
  - "../adr/IADR-0101_default-model-opus-5.md"
  - "../adr/IADR-0102_trade-decision-model-pinning.md"
  - "../adr/IADR-0022_default-opus-and-fable5-copilot-routes.md"
  - "./20260724_adr-0025_default-model-opus-5.md"
  - "./20260725_ast-adr-0011_trade-decision-model-pinning.md"
  - "../functional/FR-11_llm-egress-routing.md"
  - "../tests/FR-11_llm-egress-routing.md"
---

# 仕様書: `rag-answer` の Sonnet 5 追随（issue #381）

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#381](https://github.com/endazon/microservices-platform/issues/381)
  （`feat(ADR-0022)`・label `enhancement` / `priority:should`）。[[IADR-0101]] §フォローアップ 4 の消化。
- 計画根拠: [ADR-0022](../../planning/projects/microservices-platform/07_adr/ADR-0022_llm-model-sonnet-5.md)
  （定型 RAG 回答を `claude-sonnet-4-6` → **`claude-sonnet-5`** へ改定・**Accepted**・2026-07-23）§決定。
  > 定型・高頻度 RAG回答の割当モデルを `claude-sonnet-4-6` → **`claude-sonnet-5`** に改定する。
  > フォローアップ: 実装側の `Llm:Routing:PurposeModels` を Sonnet 5 へ更新（IADR 起票）。
- 補強根拠: [ADR-0025](../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md) §決定は
  「他層（定型RAG回答=**Sonnet 5**、図のコード化=Haiku 4.5、最難関=Fable 5）は変更しない」と記述しており、
  **計画側の確定状態は `rag-answer` = Sonnet 5**。実装だけが取り残されている。
- 要求: FR-11（LLM 送信可否の統制・用途別ルーティング）、FR-04（AI 回答と出典）、UC-01。
- 設計: [ADR-0010](../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md)（LLM ゲートウェイ・本文凍結）、
  [[IADR-0022]]（ゲートウェイ経路・ZDR 除外）、[[IADR-0101]]（既定 Opus 5・max_tokens 4096）、
  [[IADR-0102]]（`Models` 許可一覧に載せないとピン留めが無効化される罠）。
- 本作業の実装判断は [[IADR-0106]]。

## 背景と問題（原因の確定）

`ADR-0022` は 2026-07-23 に Accepted となり、`rag-answer` の割当を Sonnet 5 へ改定した。しかし実装側の
`Llm:Routing:PurposeModels` は現在も `claude-sonnet-4-6` のままで、**計画と実装が乖離**している。

```jsonc
// src/platform/backend/Services/LlmGateway/src/LlmGateway.Api/appsettings.json:41-57
"PurposeModels": {
  "rag-answer": "claude-sonnet-4-6",   // ← ADR-0022 の確定値は claude-sonnet-5
  ...
},
"Models": [ "claude-fable-5", "claude-opus-5", "claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5" ],
```

[PR #376](https://github.com/endazon/microservices-platform/pull/376) はスコープ外として扱い、`RagOrchestrator`
のコメントと `docs/functional/FR-11_llm-egress-routing.md` の未決事項に「追随が未消化」と明記するに留めた。
本作業でそれを消化する。

### 踏んではならない罠（[[IADR-0102]] / #376 の再発防止）

`LlmRouter.ResolveModel` の用途別解決は `eligible.Contains(purposeModel)` を条件とする。

```csharp
// LlmRouter.cs:104-106
if (_options.PurposeModels.TryGetValue(request.Purpose, out var purposeModel)
    && eligible.Contains(purposeModel))
    return purposeModel;
```

`eligible` は**エンドポイントの `Models`（利用許可集合）**から導出されるため、`PurposeModels` だけを
`claude-sonnet-5` に書き換えて `Models` へ登録し忘れると、**例外にもログにもならずに `DefaultModel`
（＝`claude-opus-5`）へ黙ってフォールバック**する。単価も挙動も変わるのに検知できない。
これは [[IADR-0102]] が取引判断のピン留めで実際に踏んだ罠であり、本作業でも同じ構造を持つ。

### モデル差分（Sonnet 4.6 → Sonnet 5）

| | Sonnet 4.6 | Sonnet 5 |
| --- | --- | --- |
| `thinking` 省略時 | **思考なし** | **adaptive thinking が有効** |
| `max_tokens` の意味 | 実質「本文の上限」 | **思考トークン＋本文の合算上限** |
| トークナイザ | 従来 | **新トークナイザ（同一テキストで約 +30% トークン）** |
| 標準単価 | $3 / $15 per MTok | **同額**（2026-08-31 まで導入価格 $2 / $10） |
| 非既定サンプリングパラメータ | 可 | **不可**（`temperature` 等は 400） |
| ZDR | 対応 | **対応**（30 日保持要件は Fable 5 / Mythos 5 のみ） |

`ClaudeProvider` は `thinking` / `temperature` / `top_p` / `top_k` / assistant prefill を**一切送信していない**
（[[IADR-0101]] の決定）ため、Sonnet 5 で 400 を返す破壊的パラメータは存在しない。差分は上表の
「thinking 既定有効」「新トークナイザ」の 2 点に閉じる。

## 対象範囲

### 変更する

| 対象 | 変更内容 |
| --- | --- |
| `LlmGateway.Api/appsettings.json` | `PurposeModels.rag-answer` を `claude-sonnet-5` へ。claude エンドポイントの `Models` に `claude-sonnet-5` を**追加** |
| `LlmRoutingOptions.cs` | 用途別モデルの説明コメントを Sonnet 5 へ追随 |
| `ClaudeProvider.cs` | クラスコメントの定型用途の版数を Sonnet 5 へ追随 |
| `RagOrchestrator.cs`（knowledge） | 「ADR-0022 の Sonnet 5 追随は未消化」と明記した 2 箇所のコメントを消化済みへ更新 |
| `LlmRouterTests.cs` | 固定値フィクスチャ（`PurposeModels` / `Models`）を Sonnet 5 へ。T-19 を追加 |
| `CompletionRoutingEndpointTests.cs` | 用途別モデル Theory の期待値を `claude-sonnet-5` へ。`PurposeModels ⊆ Models` の恒久ガードを追加 |
| `docs/functional/FR-11_llm-egress-routing.md` | 既定設定表の版数更新。未決事項の「追随が未消化」項目を消す |
| `docs/tests/FR-11_llm-egress-routing.md` | T-02 の版数更新。T-19 を追加 |
| `docs/adr/README.md` | IADR-0106 の索引行を追加 |

### 変更しない（意図的に対象外）

- **`Models` からの `claude-sonnet-4-6` 削除**。`Models` は「用途別の割当」ではなく「このエンドポイントで
  **利用を許可するモデル集合**」であり、削除すると明示 `Model: "claude-sonnet-4-6"` を送っている
  呼び出し側が黙って別モデルへ落ちる（破壊的変更）。`Models` は既に `claude-opus-4-8`（取引判断ピン留め）
  など複数版数を並存させており、追加のみが非破壊。
- **既定 `max_tokens`（4096）の変更**。後述の検証で現時点は据え置きが妥当と判断（実測は #380）。
- **`NonZdrModels` への `claude-sonnet-5` 追加**。Sonnet 5 は ZDR 対応（後述）。
- **`RagOrchestrator` の縮退経路のモデルラベル**（issue #381「併せて見直す（任意）」）。後述の理由で見送り。
- 他層（`analysis` / `diagram-coding` / `trade-decision` / `default`）の割当。ADR-0025 §決定により不変。
- マージ済みの point-in-time 記録（`docs/specs/20260706_*`・`feedback/*`・[[IADR-0022]] 本文）の追随改変。
- `realm.json`（本作業と無関係）。

## 受け入れ基準（issue #381 §受け入れ基準に対応）

- [x] `PurposeModels.rag-answer` が `claude-sonnet-5`、かつ claude エンドポイントの `Models` に含まれる
- [x] 用途 `rag-answer` が `claude-sonnet-5` を返す（`DefaultModel` へ落ちない）ことをテストで固定した
- [x] Sonnet 5 の thinking 既定有効化に対し `max_tokens` が十分であることを確認した（不足なら引き上げ）
- [x] ZDR 要件区分での解決結果が意図どおりであることを確認した
- [x] 実装 ADR（[[IADR-0106]]）に記録し、機能仕様書・テスト仕様書・コード内コメントを更新した

## 検証: `max_tokens = 4096` は Sonnet 5 で足りるか

[[IADR-0101]] が 1024 → 4096 へ引き上げた根拠は「従来どおりの本文長（〜1024 相当）＋ adaptive thinking の
作業領域（〜3000）」である。Sonnet 5 で追加される劣化要因は 2 つ。

1. **thinking が既定で有効になる**（Sonnet 4.6 は `thinking` 省略時は思考なし）。
   → `max_tokens` を本文と思考で分け合う構造になる。これは Opus 5 で既に織り込み済みの前提と同じで、
   4096 はその前提のもとで設定された値である。
2. **新トークナイザで同一テキストが約 +30% トークン**になる。
   → 本文の想定枠 1024 相当が約 1331 相当へ増える。残りの思考枠は 4096 − 1331 ≈ 2765。

`[1] + [2]` を合わせても、[[IADR-0101]] が見込んだ「本文＋思考」の配分は 4096 の内側に収まる。よって
**据え置きが妥当**と判断する。ただしこれは Opus 5 と同じく**実測前の出発値**であり、実測による再調整は
[#380](https://github.com/endazon/microservices-platform/issues/380) で扱う（本作業と連動する既存 issue）。

なお `RagOrchestrator` は `MaxTokens: 4096` を**明示指定**しており（2 箇所）、共有契約の既定値に依存して
いない。本作業では両方とも据え置くため、`rag-answer` 経路の実効値は変わらない。

## 検証: ZDR 要件区分での解決結果

claude エンドポイントの `NonZdrModels` は `["claude-fable-5"]` のみ。30 日データ保持要件を持つのは
**Fable 5 / Mythos 5** であり、**Sonnet 5 は ZDR 対応**である（Sonnet 4.6 と同じ位置づけ）。よって
`NonZdrModels` は無変更でよい。

結果として `confidential`/`restricted` × `rag-answer` は、除外を受けずに `claude-sonnet-5` を選択する。
これは Sonnet 4.6 時代の挙動（T-13 の `Route_Confidential_IgnoresRequestedNonZdrModel` が固定している
「用途 rag-answer は ZDR 対応の sonnet が選ばれる」）と**同じ意味**であり、テストの意図は保たれる。

`ADR-0022` §結果のフォローアップ「データ保護ティア判定は Sonnet 5 の保持・学習利用条件を都度確認する
方針を継続」は、この確認をもって現時点では充足する。

## 見送り: 縮退経路の「使用モデル」ラベル（issue #381「併せて見直す（任意）」）

`RagOrchestrator` の縮退経路（`AskStreamAsync` の権限なし分岐・`EmptyAnswer`）は LLM を呼ばないのに
`config["Llm:DefaultModel"] ?? "claude-opus-5"` を「使用モデル」として返す。`rag-answer` の実モデルとは
異なるラベルが表示に載る、という指摘（[[IADR-0101]] レビューの 🟢1）は妥当である。

本作業では**見送る**。理由は次の 3 点。

1. issue #381 でも「併せて見直す（任意）」に置かれており、受け入れ基準に含まれていない。
2. 返却値（`AiAnswerDto.Model` / `AskDoneEvent`）を変えることは**応答契約の観測可能な挙動変更**であり、
   本作業の非破壊方針（設定値の追随に限定）から外れる。
3. そもそも `Llm:DefaultModel` というキーは `appsettings.json` に存在せず（実在するのは `Llm:Model`）、
   常にハードコードされたフォールバックへ落ちている。これは版数追随とは**別の欠陥**であり、
   「LLM を呼んでいない縮退応答が何をモデルとして名乗るべきか」という設計判断（`null` / 空 / 実解決値）を
   伴う。設定値追随の PR に混ぜると、レビューの焦点がぼやける。

[[IADR-0106]] §フォローアップに残し、別 issue で扱う。

## 実装方針（TDD）

1. **Red**: `CompletionRoutingEndpointTests` の Theory 期待値を `claude-sonnet-5` に変更する。
   このテストは `TestWebApplicationFactory` が**実 `appsettings.json` のルーティング設定を読む**ため、
   設定を変える前は失敗する。加えて「`Models` 未登録なら `DefaultModel` へ黙って落ちる」罠を
   恒久的に塞ぐガードテスト（全 `PurposeModels` の値が claude エンドポイントの `Models` に含まれること）を
   追加する。`LlmRouterTests` の固定値フィクスチャも Sonnet 5 へ更新し、T-19 を追加する。
2. **Green**: `appsettings.json` の `PurposeModels.rag-answer` と `Models` を更新する。
3. **Refactor/追随**: コード内コメント・機能仕様書・テスト仕様書・IADR・索引を更新する。
4. **検証**: `dotnet build` / `dotnet test`（platform・knowledge 両ユニット）/ `dotnet format --verify-no-changes`。

## テスト観点

| ID | 観点 | 期待 |
| --- | --- | --- |
| T-02（更新） | 用途別モデル選択 | `rag-answer` → `claude-sonnet-5` |
| T-13（意図維持） | ZDR 除外 | `confidential` × `rag-answer` は除外を受けず `claude-sonnet-5` |
| T-19（新規） | Sonnet 5 追随と `Models` 登録ガード | `rag-answer` が `claude-sonnet-5` を返し `DefaultModel` へ落ちない。`PurposeModels` の全値が claude エンドポイントの `Models` に含まれる |

## 完了条件（DoD）

- `dotnet build` / `dotnet test` が platform・knowledge 両ユニットで通る
- `dotnet format --verify-no-changes` が両ユニットで通る
- 上表の受け入れ基準がすべてチェック済み
- `docs/DEFINITION_OF_DONE.md` を満たす
