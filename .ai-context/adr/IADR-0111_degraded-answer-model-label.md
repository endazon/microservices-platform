---
title: IADR-0111 縮退応答の「使用モデル」はゲートウェイ報告値を透過し、未送信は空文字で表す
type: impl-adr
status: Accepted
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
author: claude
created: 2026-07-28
updated: 2026-07-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (LLM ゲートウェイ設計・Accepted・本文凍結)
  - planning:projects/microservices-platform/07_adr/ADR-0022_llm-model-sonnet-5.md (定型RAG回答=Sonnet 5・Accepted)
  - planning:projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md (グローバル既定=Opus 5・Accepted)
---

# IADR-0111: 縮退応答の「使用モデル」ラベル

- 状態: Accepted
- 日付: 2026-07-28
- 決定者: claude（実装）

## 起点・関連

- 起点 issue: [#403](https://github.com/endazon/microservices-platform/issues/403)（`bug` / size S）。
  [IADR-0106](./IADR-0106_rag-answer-sonnet-5.md) §フォローアップ 3 の消化（#381 / PR #402 の副次発見）。[IADR-0101](./IADR-0101_default-model-opus-5.md) レビューの 🟢1 も同一箇所を指摘。
- 仕様書: `docs/specs/20260728_issue-403_degraded-answer-model.md`。
- **採番の経緯**: 本 ADR は当初 `IADR-0108` として起票したが、作業中に
  [PR #413](https://github.com/endazon/microservices-platform/pull/413)（headlamp-viewer の閲覧専用 RBAC）が
  同番号で先に develop へマージされたため **`IADR-0111` へ改番**した（`IADR-0109` / `IADR-0110` は
  同時進行の #394 / #395 が使用）。改番前のコミット件名には `IADR-0108` が残る（force-push 禁止のため
  書き換えない）。PR 件名・本文・仕様書・コード内コメントは `IADR-0111` に揃えている。
- 本 IADR は**呼び出し側（`RagOrchestrator`）が応答契約へ載せるモデル名の決め方**のみを扱う。
  ゲートウェイのルーティング（[IADR-0022](./IADR-0022_default-opus-and-fable5-copilot-routes.md)）・用途別割当（[IADR-0106](./IADR-0106_rag-answer-sonnet-5.md)）・拒否判別（[IADR-0104](./IADR-0104_llm-stop-reason-refusal.md)）は変更しない。

## コンテキストと課題

`RagOrchestrator` は「使用モデル」として `config["Llm:DefaultModel"] ?? "claude-opus-5"` を用いていた（3 箇所）。

1. **キーが実在しない。** `Llm:DefaultModel` はリポジトリのどこにも定義がない（実在するのは
   `LlmGateway.Api/appsettings.json` の `Llm:Model` と `Llm:Routing:Endpoints[].DefaultModel`）。
   `AiAnalysisService` の `appsettings.json` は `Llm:*` セクション自体を持たない。よって `??` の左辺は
   常に `null` で、実質ハードコードの `"claude-opus-5"` が返り続けていた。
2. **値が実モデルと一致しない。** 本経路の purpose は `rag-answer`＝`claude-sonnet-5`（ADR-0022 / [IADR-0106](./IADR-0106_rag-answer-sonnet-5.md)）。
   `default` 層（Opus 5）の値は、実際に LLM を呼んだ場合とも食い違う。

結果、**LLM を一度も呼んでいない応答が `claude-opus-5` を自己申告**していた。この値は
`AiAnswerDto.Model` / `AskDoneEvent.Model` に載って SC-08 の表示・監査・利用状況集計へ流れる。
例外にもログにも出ないため、誤った値が静かに流れ続ける（権限不足で外部送信していないのに Opus 5 を
使ったかのように記録される、等）。

### 見落とされていた事実: ゲートウェイは既に正しく表現している

`CompletionEndpoints` は縮退の各経路で `Model` を次のように埋めている。

| ゲートウェイ側の経路 | `Sent` | `Model` |
| --- | --- | --- |
| 越境拒否（`decision.Allowed=false`。プロバイダ未呼出） | `false` | **`string.Empty`** |
| プロバイダ keyed DI 未登録（未呼出） | `false` | **`string.Empty`** |
| 呼び出し先が不調（例外。**呼び出しは試みた**） | `false` | `decision.Model`（実 route 結果） |
| 送信成立（`refusal` を含む） | `true` | `decision.Model`（実 route 結果） |

「送信していない＝空文字」「解決した＝route 結果」という表現は**既にゲートウェイ側にある**。
`RagOrchestrator` はそれを捨てていた（`string.IsNullOrEmpty(ev.Model) ? defaultModel : ev.Model`、
`Sent=false` 分岐では `completion.Model` を見ずに `defaultModel`）。したがって本件は新しい表現を
発明する問題ではなく、**呼び出し側が捏造をやめて透過する**問題である。

## 検討した選択肢

1. **ゲートウェイ報告値を透過し、未送信・未到達は空文字（採用）** — 値の決定権を、実際に route を
   行った唯一の場所（ゲートウェイ）へ一元化する。応答契約の型は不変（`string`）。ゲートウェイの
   既存表現と一致するため、レイヤ間で「未使用」の表し方が二重定義にならない。
2. `Model` を `string?` にして未使用は `null` — 「未使用」を型で表せて最も明示的だが、共有契約
   （`AiAnswerDto` / `AskDoneEvent`）と TS 側の型（`model: string`）を破壊的に変え、JSON に `null` が
   出現する。ゲートウェイは同じ意味を既に空文字で表しており、**同一の意味に 2 つの表現**を作ることになる。
   採らない。
3. 実在キー（`Llm:Model`）へ直し、既定層のモデル名を名乗り続ける — キーの不整合は解消するが、
   **「呼んでいないモデルを名乗る」という本質の誤りが残る**（#403 §期待に反する）。さらに `Llm:Model` は
   ゲートウェイの設定であり、AiAnalysisService から参照するとサービス境界を越えた設定の二重管理になる
   （ゲートウェイの設定変更で呼び出し側の表示が黙ってずれる）。採らない。
4. 呼び出し側で purpose→モデルの対応表を持ち、`rag-answer` なら `claude-sonnet-5` と名乗る — ルーティング
   ロジック（機密区分・ZDR 除外・エンドポイント優先度）の**部分的な複製**であり、[IADR-0106](./IADR-0106_rag-answer-sonnet-5.md) で塞いだ
   「設定とコードの二重管理による無音の乖離」を別の場所に作り直す。しかも実際には呼んでいない。採らない。

## 決定

- `RagOrchestrator` から **`config["Llm:DefaultModel"] ?? "claude-opus-5"` を 3 箇所とも削除**し、
  クラスの `IConfiguration` 依存自体を外す（存在しないキーの復活余地を残さない）。
- 縮退応答の「使用モデル」は次のとおりとする。

  | 経路 | LLM | 返す `Model` |
  | --- | --- | --- |
  | ABAC 不許可（`EmptyAnswer` / `AskStreamAsync` 権限なし分岐） | 呼ばない（ゲートウェイも呼ばない） | `""` |
  | ゲートウェイ HTTP 非 2xx・通信失敗 | 到達せず | `""` |
  | ゲートウェイが越境拒否（`Sent=false`・`Model=""`） | 呼ばない | `""`（透過） |
  | ゲートウェイが呼び出し先不調（`Sent=false`・`Model=route 結果`） | 試みて失敗 | route 結果（透過） |
  | 送信成立（`refusal` 含む） | 呼んだ | route 結果（透過。従来どおり） |

- **空文字の意味を「モデル未使用（AI へ送信していない）」と定義**し、`RagOrchestrator.NoModel` 定数と
  仕様書（FR-04 / FR-11 / SC-08）に明記する。
- 応答契約（`AiAnswerDto.Model` / `AskDoneEvent.Model`）の**型・フィールドは変更しない**。
  観測可能な変化は「縮退時の値が `claude-opus-5` から `""` または実 route 結果へ変わる」ことのみ。
- SC-08（`AnalysisDashboardPage`）は `model` が空のとき「モデル: 未使用（AI へ送信なし）」と表示する
  （`モデル: ` の後ろが空白のまま残る表示崩れを防ぎ、「未送信」を利用者に伝える）。
  SC-01（`SearchChatPage`）は `done` の `model` を**画面に表示していない**ため追随不要。
- 回帰は T-10〜T-16（`RagOrchestratorDegradedModelTests`）と T-15f（SC-08）で固定する。T-16 は
  ゲートウェイが 2xx で本文 JSON `null` を返した場合（逆シリアル化結果が null）に、`ModelOrNone` が
  `null` を応答契約へ載せず空文字へ倒すことの固定である。

## 理由

- **誰も呼んでいないモデル名を名乗らない**という #403 の要求を、値の出所を一本化することで満たす。
  モデル名を決めてよいのは route を行ったゲートウェイだけであり、呼び出し側は運び手に徹する。
- 「未送信＝空文字」は**ゲートウェイが既に採っている表現**であり、レイヤ間で意味がずれない。
  新しい語彙（`null` / `"none"` / `"n/a"`）を増やすと、集計側がどちらも解釈する必要が生じる。
- 呼び出し先不調（`Sent=false` かつ `Model=route 結果`）を空へ潰さないのは、この経路だけは
  **実際に外部呼び出しを試みており**、どのモデルへ向けた試行だったかが監査・障害解析の情報になるため。
  「送信の成否」は従来どおり `Sent` が表す（[IADR-0104](./IADR-0104_llm-stop-reason-refusal.md) の `Sent` と `StopReason` の分離と同じ考え方）。
- 契約の型を変えないため、フロント・BFF・OpenAPI の破壊的変更が発生しない。

## 結果

- 良い影響: 権限不足・越境拒否で外部送信していない応答が Opus 5 を使ったと記録される誤りが消える。
  実際に呼んだ場合は route 結果（`rag-answer`＝Sonnet 5）が正しく載る。存在しない設定キーが消え、
  「設定で差し替えられるように見えて実は不能」という誤解の余地がなくなる。
  `RagOrchestrator` の依存が 1 つ減る（`IConfiguration` 不要）。
- 悪い影響 / トレードオフ:
  - **応答契約の観測可能な挙動変更**である。縮退時の `model` が `"claude-opus-5"` から `""` へ変わるため、
    この値でグルーピングしている集計・ダッシュボードがあれば空文字の系列が現れる（ただし従来値は
    実態と無関係な固定値であり、集計上の意味は元々なかった）。
  - 空文字は「未使用」と「未設定/不明」を区別しない。区別が必要になったら理由コード
    （`RoutingReason` は既にゲートウェイ側にある）を応答契約へ載せる拡張が要る。本 issue の範囲では
    `Sent` 相当の情報を縮退メッセージ本文が既に伝えているため導入しない。
- フォローアップ:
  1. **縮退理由（`RoutingReason`）の応答契約への露出**は未実施。現在は縮退メッセージ本文でのみ
     利用者へ伝えている。監査・可観測性の要求が具体化したら別 issue で扱う。
  2. 利用状況集計（Grafana 等）で `model` を次元に使う場合、空文字系列の扱いを定義する。

## 関連

- Supersedes: なし（[IADR-0101](./IADR-0101_default-model-opus-5.md) / [IADR-0106](./IADR-0106_rag-answer-sonnet-5.md) の決定は不変。両者のレビュー指摘・フォローアップの消化）
- Superseded by: なし
- 関連要求 / UC: FR-04（AI 回答と出典）、FR-11（LLM 送信可否の統制）、FR-05（ABAC）、UC-01 / UC-02、SC-08
