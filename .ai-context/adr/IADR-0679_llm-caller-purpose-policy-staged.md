---
title: IADR-0679 LLM ゲートウェイの補完は、呼び手ごとの許可表（用途・モデルの明示の指定）を REST と gRPC の両面で 1 つの判定器が見る。段 1 は報告、段 2 は 403 / PERMISSION_DENIED（#2417）
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-09, FR-11, ADR-0084, ADR-0010, IADR-0379, IADR-0424]
author: claude
created: 2026-11-05
updated: 2026-11-05
related_specs:
  - ../specs/20261105_2417_llm-caller-purpose-policy.md
---

# IADR-0679: LLM ゲートウェイの補完の呼び手ごとの許可表（#2417）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: claude（#2417。コーディネーターの指示 2026-11-05）

## 起点・関連

- 起点 issue: #2417。
- 前提: 計画 ADR-0084 決定 1（端点ごとの門）。IADR-0379 決定 4・IADR-0424（補完の 2 面は同じ `ServiceCaller`）。
- 作業仕様書: `.ai-context/specs/20261105_2417_llm-caller-purpose-policy.md`。

## コンテキスト

- `ServiceCaller` は realm ロール `platform-service` を見る。realm ではこのロールを、補完を呼ばないサービス（取り込み・Wiki・データソース・文書・MCP 等）のサービスアカウントにも与えている。
- 呼び手を区別する判定は無く、どの主体もすべての用途と、モデルの明示の指定を使えた。

## 決定

### 決定 1 — 許可表は `Llm:CallerPolicy:Callers`（呼び手 → 用途の一覧・`AllowModelOverride`）

- 呼び手は検証済みトークンのクライアント識別子（`MachinePrincipal.ClientIdOf`）。
- 用途は**要求の原文**（未指定は `default`。大文字小文字は区別しない）で照合する。計器の正規化（未知 → `other`）は使わない。正規化した値で照合すると、未知の用途が 1 つの枠に寄り、許可表の外の用途が通り得る。
- 既定の表は、呼び手のコードが実際に使う用途から引いた（作業仕様書 §母集合）。AST の呼び手だけは、モデルを自分で固定する設計なので、`AllowModelOverride` を許す。

### 決定 2 — 判定器は `LlmCallerPolicy` の 1 つで、REST と gRPC の両面が判定器（CompletionUseCase）に入る前に呼ぶ

- 違反の種類は 3 値（`unknown_caller`・`purpose_not_allowed`・`model_override_not_allowed`）。計器 `llm.caller_policy.violation.total`（種類・段・呼び手・正規化した用途）と、5 分に 1 行の Warning を出す。

### 決定 3 — 段は `Llm:CallerPolicy:Mode`。既定は `Report`

- `Report`（段 1）: 数えるだけで、通す。
- `Enforce`（段 2）: REST は 403、gRPC は `PERMISSION_DENIED` で拒む。認可の失敗なので、`Sent=false` の縮退ではなく状態コードで返す。共有契約は変えない。

## 結果・残余リスク

1. 段 1 のあいだは、許可表の外の呼び出しも通る。段 2 へ切り替える前に、計器で `llm_policy_mode="report"` の違反が想定外の呼び手から出ていないことを確かめる。
2. 呼び手が用途を足すときは、この表も同じ PR で足す必要がある（足さないと段 2 で拒まれる）。
3. 埋め込みの面は射程外。
