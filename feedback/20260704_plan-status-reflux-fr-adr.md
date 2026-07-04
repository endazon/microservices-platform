---
title: 計画書ステータスの環流 — 全計画書 draft・全 ADR Proposed のまま FR-01〜13 実装完了、状態確定と実装差分を提案
type: plan-feedback
status: open
category: 要求の不足
related_ids:
  - FR-01
  - FR-02
  - FR-03
  - FR-04
  - FR-05
  - FR-06
  - FR-07
  - FR-08
  - FR-09
  - FR-10
  - FR-11
  - FR-12
  - FR-13
  - ADR-0002
  - ADR-0010
  - ADR-0011
  - SC-01
  - SC-02
  - SC-03
  - SC-04
  - SC-05
  - SC-06
  - SC-07
  - SC-08
  - SC-09
  - SC-10
source_repo: microservices-platform
source_ref: "branch claude/issue-57-20260704-0226 / Issue #57（親 #48）"
author: claude
created: 2026-07-04
---

# フィードバック: 計画書ステータスの環流（状態確定と実装差分の反映）

## 種別

要求の不足（状態遷移の欠落）＋ 要求/ADR の差異。実装が完了しているにもかかわらず、計画書の状態が
`draft` / `Proposed` のまま据え置かれており、実装の正当性の根拠（fixed / Accepted な計画）が未確定である。
併せて、実装で判明した計画との差分を環流する。

## 起点となる計画書

- 機能要求（FR）: FR-01〜FR-13（`02_requirements/01_requirements.md`）— 全件が実装リポでマージ済み。
- ユースケース（UC）: UC-01〜UC-07（`03_usecases/01_usecases.md`）。
- 画面（SC）: SC-01〜SC-10（`05_screens/01_screens.md`）— **全件フロントエンド未着手**。
- 関連 ADR: ADR-0001〜ADR-0014（`07_adr/`）— 全件 `Proposed`。特に差分は ADR-0002 / ADR-0010 / ADR-0011。
- 計画書リンク:
  - `projects/microservices-platform/02_requirements/01_requirements.md`
  - `projects/microservices-platform/07_adr/ADR-0002_service-boundaries-db-per-service.md`
  - `projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md`
  - `projects/microservices-platform/05_screens/01_screens.md`

## 現状（計画書の記述 / As-Is）

- 計画リポジトリ側は**全ナラティブ文書が `status: draft`**、**ADR-0001〜0014 が全て `status: Proposed`** のまま。
- 一方、実装リポジトリでは **FR-01〜FR-13 の全てがマージ済み**（11 サービス＋BFF を配備）。
- `02_requirements/01_requirements.md` L94 に「注: 関連ADR（ADR-0001〜0014）は作成・**確定済み**であり、番号は確定値である」
  との記述があるが、ADR 本文の状態は全て `Proposed` で、**要求書の「確定済み」表記と ADR 本文の状態表記が不整合**。
- CLAUDE.md の運用は「fixed / Accepted な計画に忠実に実装」を前提としており、現状は実装の正当性の根拠が未確定状態。

## 問題点 / あるべき姿（To-Be）

実装が完了した範囲について、計画書の状態を確定（`fixed` / `Accepted`）へ更新し、実装の正当性の根拠を確定させるべきである。
併せて、実装で計画と乖離した箇所を計画へ環流し、状態確定と同時に整合させる。

### あるべき状態遷移

- 実装済み FR に対応する要求書・UC・技術検討・業務フローの状態を `draft` → `fixed`（確定）へ更新する。
- 実装が依拠した ADR の状態を `Proposed` → `Accepted` へ更新する。
  - 少なくとも実装で参照・充足済みの **ADR-0002 / 0003 / 0004 / 0009 / 0010 / 0012 / 0013 / 0014** を対象とする。
  - ADR-0011 は Wiki.js 逸脱により **`Accepted` ではなく `Superseded`**（後述、別フィードバック参照）。
  - ADR-0001（マイクロサービス採用）/ 0005（Istio）/ 0006（可観測性）/ 0007（GitOps/ArgoCD）/ 0008（k3s）は、
    実装の充足度に応じて計画側で個別に判断する（本フィードバックは状態確定の起票を促すもので、確定判断は計画側）。
- SC-01〜SC-10 は**未着手のため `fixed` にせず `draft`（または `review`）のまま**とし、ロードマップ上の位置づけを明記する。

## 実装で判明した経緯（環流すべき差分）

### 差分1: サービス数 — ADR-0002「8＋BFF」に対し実態 11＋BFF

- ADR-0002 の決定は「文書管理／データソース連携／変換／取り込み／検索／AI分析／認可／Wiki の **8サービス＋BFF**」、
  「結果」節で「サービス数は **8前後を上限の目安**とする」と記載。
- 実装の `src/Services/` は **11 サービス＋BFF**:
  - 計画の 8: `DocumentService` / `DataSourceService` / `ConversionService` / `IngestionService` /
    `RetrievalService` / `AiAnalysisService` / `AuthorizationService` / `WikiService`
  - 追加の 3: `LlmGateway`（ADR-0010 で決定した LLM ゲートウェイの実体）、
    `FeedbackService`（FR-08、実装判断 IADR-0010）、`DashboardService`（FR-10、実装判断 IADR-0011）
- いずれも計画由来の要求（FR-08 / FR-10 / FR-11）に基づく分割であり、過剰分割ではない。実装判断は IADR-0010 / IADR-0011 に記録済み。

### 差分2: LLM 既定モデル — ADR-0010 の記載と実装既定の不一致

- ADR-0010 の決定文: 既定は外部マネージドAPI「Claude SDK＝既定 `claude-opus-4-8`、定型は
  `claude-sonnet-4-6` / `claude-haiku-4-5`、最難関は `claude-fable-5`／GitHub Copilot SDK」。
- 実装の既定（`LlmGateway` `appsettings.json` / `ClaudeProvider` / `RagOrchestrator`）:
  - グローバル既定モデル: `claude-sonnet-4-6`（`Llm:Model` / `Llm:DefaultModel` の既定値）
  - 用途別（`Llm:Routing:PurposeModels`）: `rag-answer → claude-sonnet-4-6`、`analysis → claude-opus-4-8`、
    `diagram → claude-haiku-4-5`
  - **`claude-fable-5`（最難関）および GitHub Copilot SDK は未実装**（`src/` 配下に参照なし）。
- すなわち「既定 = opus」という ADR-0010 の記載に対し、実装は「既定 = sonnet、最難関用途 analysis のみ opus」を採用しており、
  最難関 fable-5 / Copilot 経路は未導入。挙動自体は用途別ルーティング（IADR-0007）で統制されているが、
  **既定モデルの選定意図が計画・実装で食い違っている**。
- **実装側の見解**: 現行の用途別ルーティングで FR-06（RAG 回答）/ FR-07（AI 分析）の要件は充足しており、
  既定を opus へ引き上げる必要はない（コスト増のみで便益が薄い）。fable-5 / Copilot 経路も現時点で対応する用途が
  無く、追加は過剰実装と判断する。したがって**実装の現状（既定 sonnet・用途別ルーティング）を真実源とし、
  計画 ADR-0010 を実態に追認する方向を推奨する**（後述の提案4 (a)）。

### 差分3: 画面（SC-01〜SC-10）フロントエンド未着手

- 実装はバックエンド（API / サービス）が中心で、**SC-01〜SC-10 の画面（フロントエンド）は全件未着手**。
  #48 の横断監査でコード・PR からの SC 参照がゼロであることを確認済み。
- FR は API レベルで充足しているが、画面レイヤは別フェーズ。計画側のロードマップに「SC はバックエンド確定後の
  後続フェーズ」である旨を明記し、SC 群を `fixed` にせず据え置く根拠を残すべき。
- 補足: SC-04 は「Wiki.js での閲覧」と記載しているが、実装は自前の軽量読み取り閲覧 API（差分4）。SC-04 の文言も併せて要更新。

### 差分4: ADR-0011（Wiki.js）逸脱 — 別フィードバックで環流済み

- 実装は Wiki.js を配備せず、`WikiService` が自前 DB に正規化 Markdown を保持し読み取り専用の閲覧 API を提供。
- 本差分は既に `feedback/20260703_wiki-selfhosted-supersedes-adr-0011.md`（Issue #56、実装判断 IADR-0013）で
  「ADR-0011 を `Superseded` とし後継 ADR を起票」する形で環流済み。本フィードバックでは状態確定の観点から
  「ADR-0011 は `Accepted` ではなく `Superseded`」である点のみ再掲する。

## 提案（計画への反映案）

- 反映先候補: 要求更新（状態遷移）＋ ADR 状態更新＋ 要求書の状態表記是正＋ ロードマップ追記。
- 提案内容:
  1. **状態確定**: 実装済み FR（FR-01〜13）に対応する `02_requirements` / `03_usecases` / `04_workflows` /
     `06_technical` の各文書を、`draft` → `review` → `fixed` の遷移で確定する。
  2. **ADR の Accepted 化**: ADR-0002 / 0003 / 0004 / 0009 / 0010 / 0012 / 0013 / 0014 を `Accepted` に更新する。
     ADR-0001 / 0005 / 0006 / 0007 / 0008 は実装充足度に応じて計画側で判断する。
     - **状態遷移の順序（重要）**: 計画リポの ADR 運用規約（`planning/.claude/rules/adr.md`「Accepted 後に本文を実質変更
       しない。変更が必要なら新 ADR を起こす」）に従い、本文改訂が必要な ADR-0002 / ADR-0010 については
       **「先に `Proposed` のまま本文を実状態に合わせて改訂 → その後 `Accepted` 化」の順**で行う。
       `Accepted` 化を先行させると、後続の本文改訂が規約違反（新 ADR 起票が必要）となり手戻りになるため。
       本文改訂を要しない ADR-0003 / 0004 / 0009 / 0012 / 0013 / 0014 はそのまま `Accepted` 化してよい。
  3. **ADR-0002 の更新**（`Accepted` 化の前に実施）: 「8前後を上限の目安」を実態（**11＋BFF**）に合わせて改訂するか、
     新規追加 3 サービス（LlmGateway / Feedback / Dashboard）を明記して目安を更新する。追加は FR-08 / FR-10 / FR-11
     由来である旨を追記。本改訂を反映した上で `Accepted` 化する（提案2の順序に従う）。
  4. **ADR-0010 の既定モデル整合**（`Accepted` 化の前に実施）: 以下のいずれかを計画側で決定する。
     - **実装側の推奨は (a)**。現行の用途別ルーティング（IADR-0007: `rag-answer→sonnet` / `analysis→opus` /
       `diagram→haiku`）で FR-06 / FR-07 の要件を充足しており、既定 opus への引き上げはコスト増、
       fable-5 / Copilot 経路の追加は現時点で用途が無く**過剰実装**と判断する。よって実装の現状を真実源とし、
       計画（ADR-0010）を実態に追認する (a) を推奨する。
     - (a) 計画更新〔推奨〕: 「既定 = `claude-sonnet-4-6`、最難関用途 = `claude-opus-4-8`、fable-5 / Copilot は将来拡張」
       と実態に合わせて ADR-0010 を改訂し、改訂後に `Accepted` 化する（提案2の順序に従う）。
     - (b) 実装追従: 計画（既定 opus / 最難関 fable-5 / Copilot）を正とする場合は、実装側で既定モデル変更と
       fable-5 / Copilot 経路の追加を別 Issue 化し、実装側 IADR を起票する。この場合も ADR-0010 本文は現行のまま
       `Accepted` 化してよい（本文改訂を伴わないため順序制約は生じない）。
     - いずれにせよ「既定モデルの選定意図」を計画・実装のどちらが真実源かを確定する。
  5. **要求書の状態表記是正**: `02_requirements/01_requirements.md` L94 の「ADR-0001〜0014 は確定済み」表記を、
     実際の ADR 状態（Accepted 化後）と一致させる。後継 ADR 採番（ADR-0011 の Supersede に伴う 0015）も反映する。
  6. **SC ロードマップ追記**: SC-01〜SC-10 はフロントエンド未着手であり、バックエンド確定後の後続フェーズである旨を
     `05_screens` またはロードマップ（`06_technical/06_migration-roadmap.md`）に明記し、SC 群は `fixed` にせず据え置く。
     SC-04 の「Wiki.js」表現は自前 Wiki 閲覧 API に更新する（差分4 と整合）。
  7. **ADR-0011**: 別フィードバック（`20260703_wiki-selfhosted-supersedes-adr-0011.md`）の通り `Superseded` とする。

## 影響範囲

- 計画: 全ナラティブ文書・全 ADR の状態、ADR-0002（サービス数）/ ADR-0010（既定モデル）の本文、要求書 L94 の整合、
  SC ロードマップ、後継 ADR 採番。確定判断（fixed / Accepted）は計画側（人間 + `/triage-feedback`）が行う。
- 実装: 状態確定は追認であり挙動変更なし。差分2（既定モデル）で (b) を選ぶ場合のみ実装変更（別 Issue / IADR）が発生。
- 他 ADR: ADR-0011 は `Superseded`（#56 / IADR-0013）で確定済み。ADR-0002 / 0010 の更新は既存実装と矛盾しない。

---

## 計画リポジトリ起票用 Issue 案（`endazon/project-planning`「計画へのフィードバック」テンプレート）

**タイトル**: `[feedback/状態確定] FR-01〜13 実装完了に伴う計画書の fixed / ADR Accepted 化と実装差分の反映`

**本文**:

> - 起点 ID: FR-01〜13, ADR-0001〜0014, SC-01〜10（実装 Issue: endazon/microservices-platform#57, 親 #48）
> - 種別: 要求の不足（状態遷移）＋ 要求/ADR の差異
> - 現状: 実装リポでは FR-01〜13 が全件マージ済みだが、計画側は全ナラティブ文書が `draft`、ADR-0001〜0014 が
>   全て `Proposed`。要求書 L94 は「ADR は確定済み」と記すが本文は `Proposed` で不整合。
> - 提案:
>   1. 実装済み FR に対応する要求/UC/技術/フロー文書を `fixed` へ確定。
>   2. ADR-0002 / 0003 / 0004 / 0009 / 0010 / 0012 / 0013 / 0014 を `Accepted` 化（ADR-0011 は `Superseded`、#56）。
>      本文改訂を要する ADR-0002 / ADR-0010 は **`Proposed` のまま本文改訂 → その後 `Accepted` 化**の順とする
>      （`adr.md`「Accepted 後に本文を実質変更しない」規約に従い手戻りを防ぐ）。
>   3. ADR-0002: サービス数の目安を実態「11＋BFF」（+LlmGateway / +Feedback[IADR-0010] / +Dashboard[IADR-0011]）へ更新。
>   4. ADR-0010: 既定モデルの不一致（計画 既定 opus / 最難関 fable-5 / Copilot ↔ 実装 既定 sonnet, fable-5・Copilot 未実装）を
>      解消。**実装側は実態追認（計画更新, 提案4(a)）を推奨**（現行の用途別ルーティングで要件充足済み、fable-5 / Copilot は過剰実装と判断）。
>   5. SC-01〜10 はフロントエンド未着手のため `fixed` にせず、後続フェーズである旨をロードマップに明記。SC-04 の「Wiki.js」表現を更新。
>   6. 要求書 L94「ADR-0001〜0014 は確定済み」表記を実状態と整合（Supersede 採番 0015 も反映）。
> - 根拠: 実装は FR-01〜13 を API レベルで充足済み。実装判断は IADR-0007/0009/0010/0011/0013 に記録済み。詳細は実装側フィードバック参照。
