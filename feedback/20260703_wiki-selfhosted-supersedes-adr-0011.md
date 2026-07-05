---
title: Wiki 閲覧は自前軽量読み取り API を正式採用 — ADR-0011（Wiki.js 採用）の Supersede 提案
type: plan-feedback
status: open
category: 新たな制約(ADR要)
related_ids:
  - FR-13
  - UC-07
  - ADR-0011
  - IADR-0009
  - IADR-0013
source_repo: microservices-platform
source_ref: "branch claude/issue-56-20260703-1411 / Issue #56（親 #48）/ docs/adr/IADR-0013_wiki-selfhosted-read-api-supersedes-adr-0011.md"
author: claude
created: 2026-07-03
updated: 2026-07-05
superseded_by: ./20260705_wiki-js-deployment-follows-adr-0011.md
---

> **⚠️ 取り下げ（withdrawn, 2026-07-05）**: Issue #66 で人間が正規化方針 **(a) Wiki.js 配備**を選択したため、
> 本フィードバック（(b) ADR-0011 の Supersede 提案）は取り下げる。ADR-0011 は Supersede せず **`Proposed`→`Accepted`**
> 化を提案する後継フィードバック [20260705_wiki-js-deployment-follows-adr-0011](./20260705_wiki-js-deployment-follows-adr-0011.md)
> に置き換える。実装側は [IADR-0013] を Superseded とし [IADR-0020] で ADR-0011 に追従する。

# フィードバック: ADR-0011（Wiki.js 採用）を Supersede し、自前軽量読み取り閲覧 API を正式決定とする

## 種別

新たな制約(ADR要) — 確定していた技術決定（ADR-0011）を実装が覆しており、後継 ADR で正規化が必要。

## 起点となる計画書

- 機能要求（FR）: FR-13（正規化文書を Wiki サービスで閲覧。ABAC・横断検索・AI 回答と統合）
- ユースケース（UC）: UC-07（Wiki で閲覧する）
- 画面（SC）: Wiki 閲覧画面（`05_screens/01_screens.md`）
- 関連 ADR: ADR-0011（閲覧基盤に Wiki.js 採用）、ADR-0004（ABAC / deny-by-default）
- 計画書リンク: `projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md`

## 現状（計画書の記述 / As-Is）

- ADR-0011 は「閲覧基盤に **Wiki.js** を採用し、閲覧・編集の実体を Wiki.js へ委譲。`WikiService` は同期・統合に責務限定。ABAC は本システムが真実源、Wiki.js 側は表示制御」と決定している（状態: `Proposed`）。
- 一方 `02_requirements/01_requirements.md` の注記は「関連 ADR（ADR-0001〜0014）は作成・確定済みであり、番号は確定値である」と記す。ADR-0011 本文の `Proposed` と要求側の「確定済み」表記に**不整合**がある。

## 問題点 / あるべき姿（To-Be）

実装（`microservices-platform`）では Wiki.js を配備しておらず、`WikiService` が自前 DB（`wiki_svc`）に正規化 Markdown を保持し、読み取り専用の閲覧 API を自ら提供している。ABAC の一元管理・deny-by-default・404 存在秘匿（IADR-0009）は正しく実装され、機密性要件は満たしている。

あるべき姿は、この実装方式（自前の軽量読み取り閲覧 API）を**正式な決定として計画に反映**し、ADR-0011 を Supersede することである。理由:

- ADR-0011 自身が Wiki.js のトレードオフとして「Wiki.js の権限はページ／グループ単位であり、属性ベース（ABAC）の細粒度判定は本システム側で担保する必要がある」と認めていた。自前 API は ABAC を単一の真実源で評価し、この**認可の二重管理リスクを構造的に排除**する。
- FR-13 / UC-07 は「閲覧」に限定され、編集は UC-03（文書管理）側。Wiki.js の編集 UI・独自認証・ストレージ同期は要件に対し過剰で、運用負荷（新ミドルウェア・OIDC 連携・同期整合性）を増やす。
- Wiki.js 配備は現状の fail-closed 設計と逆行し、上記リスクを再導入する。

## 実装で判明した経緯

- Issue #48 の横断監査（`adr-guardian`）が ADR-0011 逸脱を検出 → Issue #56 として起票。
- 実装側の判断を [IADR-0013](../docs/adr/IADR-0013_wiki-selfhosted-read-api-supersedes-adr-0011.md) に記録（自前軽量閲覧 API を採用、ADR-0011 の Supersede を計画へ提案）。
- 閲覧経路の ABAC 適用自体は既存 PR #65 / [IADR-0009] で完了済み。本フィードバックは挙動変更ではなく「決定の正規化」を求めるもの。

## 提案（計画への反映案）

- 反映先候補: **新 ADR（後継）** ＋ ADR-0011 の状態更新、および要求ドキュメントの状態表記是正。
- 提案内容:
  1. **後継 ADR を起票**（例: `ADR-0015 閲覧基盤に自前の軽量読み取り専用 Wiki 閲覧 API を採用`）。決定「正規化済み Markdown を対象とする自前の読み取り専用閲覧 API を採用。ABAC を本システム側の単一真実源で評価。編集は範囲外（UC-03）」を記載し、`Supersedes ADR-0011` を明記する。
  2. **ADR-0011 を `Superseded` に更新**し、`Superseded by ADR-0015` を追記する。
  3. `02_requirements/01_requirements.md` の「ADR-0001〜0014 は確定済み」表記と ADR-0011 の `Proposed` 表記の不整合を是正する（後継 ADR 採番で 0015 まで拡張される点も反映）。
  4. FR-13 / UC-07 / `05_screens` の「Wikiサービス（既存OSS）」という表現を「自前の Wiki 閲覧 API」に更新する。
- 代替案（不採用推奨）: (a) Wiki.js を配備して `WikiService` を同期責務へ縮退。→ 認可の二重管理リスクを再導入し、要件（閲覧のみ）に対し過剰なため実装側は非推奨。計画側で (a) を選ぶ場合は、実装は Wiki.js 配備・OIDC 連携・同期方式の別 PR が必要になる。

## 影響範囲

- 計画: ADR-0011 の状態、後継 ADR の採番、FR-13 / UC-07 / 画面設計の文言、要求注記の整合。
- 実装: 追認により挙動変更なし。`docs/adr/IADR-0013`・`docs/functional/FR-13_wiki-browsing.md`・`docs/operations/operations.md`・deploy コメントを本 PR で整合済み。
- 他 ADR: ADR-0004（ABAC 真実源）とは整合。矛盾なし。

---

## 計画リポジトリ起票用 Issue 案（`endazon/project-planning`「計画へのフィードバック」テンプレート）

**タイトル**: `[feedback/ADR要] ADR-0011 を Supersede — Wiki 閲覧は自前軽量読み取り API を正式採用`

**本文**:

> - 起点 ID: FR-13, UC-07, ADR-0011（実装側記録: IADR-0013 / 実装 Issue: endazon/microservices-platform#56, 親 #48）
> - 種別: 新たな制約(ADR要)
> - 現状: ADR-0011 は Wiki.js 採用を決定（`Proposed`）だが、実装は Wiki.js 非配備で、WikiService が自前 DB に正規化 Markdown を保持し読み取り専用閲覧 API を提供している。ABAC 一元管理・deny-by-default・404 存在秘匿は実装済みで機密性は充足。
> - 提案:
>   1. 後継 ADR（例 ADR-0015）を起票し「自前の軽量読み取り専用 Wiki 閲覧 API を採用。ABAC は本システム側の単一真実源」を決定、`Supersedes ADR-0011`。
>   2. ADR-0011 を `Superseded`（`Superseded by ADR-0015`）に更新。
>   3. FR-13 / UC-07 / 画面設計の「既存OSS Wiki」表現を「自前 Wiki 閲覧 API」に更新。
>   4. `02_requirements/01_requirements.md` の「ADR-0001〜0014 は確定済み」と ADR-0011 `Proposed` の不整合を是正。
> - 根拠: ADR-0011 自身が指摘した「認可の二重管理リスク」を自前 API が構造的に排除。要件は閲覧のみ（編集は UC-03）で Wiki.js は過剰。詳細は実装側 IADR-0013 を参照。
