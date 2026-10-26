---
title: 作業仕様書 — AI 提案の生成で LLM へ送る直前に、封入した文書 ID の集合・起点・要求者を監査ログへ 1 行残す（本文・題名は載せない）（#2119）
type: spec
status: done
related_ids: [FR-18, FR-05, UC-10, ADR-0034, ADR-0051, ADR-0010, IADR-0266]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2119"
---

# 作業仕様書 — AI 提案の LLM 送信の監査（#2119）

> 本仕様書は実装着手前に作成した（着手 2026-10-26）。親は #2047（指摘 M-22）。

## 現状（origin/develop で再確認）

- `GraphService/Features/AiSuggestions/Generate/AiSuggestionGenerator.cs` にログ・監査の呼び出しが 0 件である。GraphService は `IAuditLogger` を登録していない。
- 越境は型の門（`SuggestionPrompt.Seal`。封しか LLM クライアントへ渡せない）と試験（送信本文の否定形）で塞がれているが、**稼働時に「何を送ったか」を追えない**。
- LLM ゲートウェイの判定ログ（`LlmRouter`）には文書 ID が無い。ゲートウェイは文書の集合を知らない（受け取るのはプロンプトの文字列だけ）。
- 計画 FR-18 の受け入れ基準「スコープ外本文が LLM へ送信されないことを監査ログで確認できる」と ADR-0034（決定 5「送信そのものが違反」）。

## 方針

- `AiSuggestionGenerator` に `IAuditLogger` を足す。**[4] 封の後・[5] LLM の前**に 1 行を残す。
  - action `graph.ai_suggestion.llm_send`、subject は要求者（スコープの `UserId`）、outcome `sent`。
  - detail は `origin=<起点の文書 ID> documents=<封に入った候補の文書 ID（封の順）> count=<候補の数>`。
  - 🔴 **本文・題名・辞書の値は載せない。** 文書 ID は GUID で、利用者の資料に由来する文字列ではない。`AuditLogger` が 4 引数とも無害化する（NFR-28 の log-forging 対策と同じ経路）。
  - 🔴 **封から書く。** 候補列挙の結果や類似度の結果からは書かない。ログの集合が「実際に送ったもの」と同じでなければ、監査で確かめたことにならない。
  - 送信の**前**に書く。LLM の失敗（縮退）でも「送ろうとした」行が残る（送信の試みそのものが監査の対象である。ADR-0034 決定 5）。
  - LLM を呼ばない経路（スコープ未付与・起点が見えない・候補 0・封が空）では書かない（送っていない）。
- 🔴 **件数を漏らさない**（ADR-0051 決定 2）: 監査ログは管理者向けの記録であり応答ではないが、「スコープで落とした件数」は書かない（封に入った数だけ）。
- GraphService の `Program.cs` に `IAuditLogger` を登録する（DocumentService と同じ）。
- LLM ゲートウェイは変えない（文書の集合を知らない。呼び出し側で残すのが正しい置き場所）。

## 受け入れ基準

- [x] 提案の生成 1 回（LLM を 1 回呼ぶ）につき監査ログが 1 行出る。要求者・起点・封に入った文書 ID の集合を含み、本文・題名を含まない。修正前は 0 行で赤
- [x] 監査ログの文書 ID の集合は、実際の送信本文に現れる候補の ID の集合と一致する（封から書いている）。スコープ外の ID は現れない
- [x] LLM を呼ばない経路（起点が見えない・スコープ未付与・候補 0）では監査ログを出さない（陰性対照）
- [x] LLM が縮退しても行は残る
- [x] テスト仕様書を改める

## 母集合（規則 9）

`git grep -n "ISuggestionLlmClient\|IClusterSummaryLlmClient\|LlmCompletion.LlmCompletionClient"`（GraphService）で LLM へ文書を送る経路を引いた。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `AiSuggestionGenerator`（提案の生成） | **直す** | 本件（FR-18） |
| クラスタ要約（`IClusterSummaryLlmClient`・`LlmGatewayGrpcClusterSummaryClient`） | 外す（報告） | FR-17 の経路で、本件の受け入れ基準（FR-18）の外である。同じ形の監査が要るかは別に判断する |
| `LlmRouter`（LLM ゲートウェイの判定ログ） | 外す | 文書の集合を知らない（プロンプトの文字列だけを受け取る） |

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。
