---
title: サービス間内部 API の暫定運用（ネットワーク分離）と NFR 草案（全 API OIDC/JWT・サービス間 mTLS）の相違 — NFR のフェーズ分けと ADR-0005 確定を提案
type: plan-feedback
status: accepted
category: 新たな制約(ADR要)
related_ids:
  - FR-05
  - NFR
  - ADR-0004
  - ADR-0005
source_repo: microservices-platform
source_ref: "branch claude/issue-62-20260705-0347 / PR #79 / IADR-0017 / docs/specs/20260704_NFR_internal-service-auth-network-isolation.md（Issue #62、親 #48、関連 #55）"
author: claude
created: 2026-07-05
updated: 2026-08-05
---

# フィードバック: 内部 API の暫定運用（ネットワーク分離）と NFR 草案の相違

## 種別

新たな制約（ADR 要）＋ 要求（NFR）の不足（フェーズ分けの欠落）。実装で採用した暫定方針（IADR-0017）が
NFR 草案の記述と実質的に相違しており、かつ暫定→恒久の解消時期が未 Accepted の計画 ADR に従属している。

## 起点となる計画書

- 機能要求（FR）: FR-05（機密性・アクセス制御）
- 非機能要件（NFR）: セキュリティ（認証・認可 / 通信暗号化）
- 関連 ADR: ADR-0004（ABAC 認可）= `Proposed`、ADR-0005（Service Mesh / Istio mTLS）= `Proposed`
- 計画書リンク:
  - `projects/microservices-platform/02_requirements/01_requirements.md`（L49-50、`status: draft`）
  - `projects/microservices-platform/07_adr/ADR-0004_authz-abac.md`（`Proposed`）
  - `projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md`（`Proposed`）

## 現状（計画書の記述 / As-Is）

`02_requirements/01_requirements.md`（`status: draft`）の非機能要件表に、セキュリティ要件として以下が明記されている。

- L49: 認証・認可 — 「**全 API で OIDC/JWT 認証**、文書／データ単位の認可」（補足: Keycloak、最小権限）
- L50: 通信暗号化 — 「**サービス間 mTLS**、外部通信 TLS」（補足: **Istio で自動化**）

すなわち NFR 草案は「全 API で JWT 認証」「サービス間は mTLS」を目標像として記述している。
一方、これを支える ADR-0004 / ADR-0005 は**いずれも `Proposed`（未 Accepted）**であり、Istio は未実装。

## 問題点 / あるべき姿（To-Be）

本 PR（IADR-0017）は、内部サービス全 API について **JWT 必須化・mTLS を明示的に見送り**、
mesh 導入までは **ネットワーク分離（内部サービスを host 非公開・エッジ BFF のみ認証）を第一防御**とする暫定運用を採用した。
これは上記 NFR 草案（全 API JWT／サービス間 mTLS）からの**実質的な逸脱**である。

- NFR は `draft` のため CLAUDE.md の「fixed/Accepted 逸脱禁止」には直接抵触しない。
- ただし CLAUDE.md 実装フロー手順 9（計画の誤り・不足・新たな制約を見つけたら環流）の対象となる重要判断であり、
  環流しておかないと、後に ADR-0005 が別方針（例: mTLS を採らない、または時期が大きくずれる）に振れた際に手戻りが生じる。
- あるべき姿: NFR に**フェーズ（暫定／恒久）の区別**を持たせ、暫定期の許容水準（ネットワーク分離＝第一防御）と
  恒久像（全 API JWT／サービス間 mTLS）を段階として明記する。併せて恒久像の前提である **ADR-0004/0005 を Accepted 化**して、
  暫定→恒久の移行条件と時期を確定させる。

## 実装で判明した経緯

- #48 の横断監査（`adr-guardian`）で、複数の内部 API が無認証かつホストから到達可能であると検出された。
- 内部呼び出し（`RagOrchestrator` / `WikiAccessResolver` / 取り込み・変換ワーカー）は現状いずれも JWT を付与しておらず、
  特にバックグラウンドワーカーはユーザーコンテキストを持たないため、素朴な JWT 必須化（`RequireAuthorization`）は成立しない
  （全呼び出し元が 401）。client credentials の全呼び出し元実装は規模・リスクが大きく、mTLS 導入で大半が不要になる。
- そのため暫定として IADR-0017（ネットワーク分離を第一防御）を採用し、残余リスク（同一ネットワーク内からの無認証到達）を受容した。
  詳細: `docs/adr/IADR-0017_internal-service-auth-network-isolation.md`、`docs/security/security.md`。

## 提案（計画への反映案）

- 反映先候補: NFR 更新（フェーズ分け）＋ ADR 状態更新（ADR-0004/0005 の Accepted 化・優先度付け）。
- 提案内容:
  1. **NFR のフェーズ分け**: セキュリティ NFR（L49-50）に暫定／恒久のフェーズを明記する。
     - 暫定（mesh 未導入）: 内部サービスは host 非公開＝ネットワーク分離を第一防御とし、認証はエッジ（BFF）で担保。
       サービス間は平文（コンテナ／クラスタネットワーク内に限定）を許容。回帰は `NetworkIsolationTests` で担保。
     - 恒久（mesh 導入後）: 全 API OIDC/JWT ＋ サービス間 mTLS（Istio）。移行条件＝ADR-0005 の Accepted・実装。
  2. **ADR-0005（Istio mTLS）の確定を優先課題化**: 恒久像の前提であり、暫定運用の残余リスク解消時期を決める律速。
     計画側で `Proposed` → `Accepted`（または代替方針の決定）を優先的に判断することを提案する。
  3. **ADR-0004（ABAC 認可）の確定**: エッジ認証・認可の恒久方針の前提として併せて Accepted 化を判断する。
  4. 暫定運用を「許容する」旨を NFR または関連 ADR に残し、実装（IADR-0017）との整合を明文化する。

## 影響範囲

- 計画: NFR 表（L49-50）のフェーズ分け、ADR-0004/0005 の状態・優先度。確定判断は計画側（人間 + `/triage-feedback`）。
- 実装: 本フィードバックは環流のみで挙動変更なし。ADR-0005 が別方針に振れた場合、IADR-0017 のフォローアップ（mTLS 実装 or
  client credentials）の設計に影響する。
- 関連: RetrievalService `/search`（#55）、IADR-0017、`feedback/20260704_plan-status-reflux-fr-adr.md`（ADR 群の Accepted 化提案）。

---

## 計画リポジトリ起票用 Issue 案（`endazon/project-planning`「計画へのフィードバック」テンプレート）

**タイトル**: `[feedback/新たな制約] 内部 API の暫定運用（ネットワーク分離, IADR-0017）と NFR 草案（全API JWT・サービス間 mTLS）の相違 — NFR フェーズ分けと ADR-0005 確定`

**本文**:

> - 起点 ID: FR-05, NFR, ADR-0004（Proposed）, ADR-0005（Proposed）（実装 Issue: endazon/microservices-platform#62 / PR #79、親 #48、関連 #55）
> - 種別: 新たな制約（ADR 要）＋ NFR の不足（フェーズ分け）
> - 現状: NFR 草案（`02_requirements/01_requirements.md` L49-50, draft）は「全 API OIDC/JWT 認証」「サービス間 mTLS（Istio で自動化）」を明記。
>   実装は IADR-0017 で内部 API の JWT 必須化・mTLS を暫定的に見送り、ネットワーク分離（host 非公開・エッジ BFF 認証）を第一防御に採用（NFR 草案からの実質的逸脱）。
> - 問題: 暫定→恒久の解消は ADR-0004/0005 に依存するが、両 ADR とも `Proposed`（未 Accepted）で移行時期が未確定。ADR-0005 が別方針に振れると手戻りが生じる。
> - 提案:
>   1. NFR にフェーズ分け（暫定＝ネットワーク分離を第一防御／恒久＝全 API JWT＋mTLS）を明記し、移行条件を ADR-0005 の Accepted・実装とする。
>   2. ADR-0005（Istio mTLS）の確定を優先課題化（Proposed→Accepted または代替方針決定）。
>   3. ADR-0004（ABAC 認可）を併せて Accepted 化。
>   4. 暫定運用の許容を NFR/ADR に明文化し、IADR-0017 と整合させる。
> - 根拠: バックグラウンドワーカー含む全呼び出し元が現状 JWT 非保持で、素朴な JWT 必須化は既存フローを破綻させる。詳細は実装側 IADR-0017 / security.md / 本フィードバック参照。
