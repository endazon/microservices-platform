---
title: 作業仕様書 — ADR-0011 逸脱の正規化（自前軽量閲覧 API を正式決定し ADR-0011 を Supersede 提案）
type: work-spec
status: in-progress
related_ids:
  - FR-13
  - UC-07
  - ADR-0011
  - IADR-0009
author: claude
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-13)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-07)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md"
related_specs:
  - ./20260703_FR-13_wiki-browsing-abac.md
related_adrs:
  - ADR-0011 (閲覧基盤に Wiki.js 採用。ABAC は本システム側が真実源、Wiki 側は表示制御)
  - IADR-0009 (Wiki 閲覧の 404 存在秘匿・メモリ内 ABAC 評価)
  - IADR-0013 (本作業で新設。自前軽量閲覧 API を採用し ADR-0011 を Supersede 提案)
---

# 作業仕様書: ADR-0011 逸脱の正規化

## 目的

Issue #56（親: #48 横断監査）で検出した **ADR-0011 からの設計乖離**を正規化する。

- ADR-0011 の決定は「閲覧基盤に **Wiki.js**（既存 OSS Wiki）を採用し、閲覧・編集の実体は Wiki.js へ委譲。Wiki サービスは同期・統合に責務限定」。
- 実態は Wiki.js がコード・デプロイのどこにも存在せず、`WikiService` が自前 DB（`wiki_svc`）にページを保持し閲覧 API を自ら提供している。

Issue の対応方針は (a) Wiki.js を配備し WikiService を同期責務へ縮退、または (b)「自前軽量閲覧 API へ変更」を正式決定として ADR-0011 を Supersede、のいずれかである。

## 決定と根拠

本作業は **(b) を採る**。

1. **機密性要件は現行実装で充足済み**: ABAC の一元管理・deny-by-default・404 存在秘匿（IADR-0009）は正しく実装され、Issue も「機密性要件そのものは満たしている」と認めている。問題は「確定決定からの無断逸脱」という状態である。
2. **ADR-0011 自身が Wiki.js の弱点として認可の二重管理リスクを明記**: 「Wiki.js の権限はページ／グループ単位であり、属性ベース（ABAC）の細粒度判定は本システム側で担保する必要がある」。自前軽量閲覧 API は ABAC を単一の真実源で評価し、このリスクを構造的に解消する。
3. **(a) は大規模インフラ変更でリスクを再導入**: Wiki.js 配備は新ミドルウェア（Node.js・専用 DB・OIDC 連携・ストレージ同期）の追加を伴い、現状の fail-closed 設計と逆行する。閲覧は「正規化済み Markdown の読み取りビュー」に限定されており（FR-13/UC-07 は閲覧のみ、編集は UC-03 文書管理側）、Wiki.js のフル機能は要件過剰である。
4. **ADR-0011 は計画上 `Proposed` 段階**: Accepted 前のため Supersede が整合的。ただし要求ドキュメント（`01_requirements.md`）は「ADR-0001〜0014 は作成・確定済み」とも記すため、計画側の状態表記の不整合是正も併せてフィードバックする。

最終的な計画確定（ADR-0011 の Supersede 可否）は `/triage-feedback` と人間が判断する。本作業は実装側の判断記録（IADR）と計画への反映案（feedback）を残すことを責務とする。

## 作業範囲

### 含むもの（本 PR）
- **IADR-0013** を新設し、自前軽量読み取り専用閲覧 API を採用する実装判断を記録（ADR-0011 の実装側 Supersede 判断）。
- **plan-feedback** 記録（`feedback/20260703_wiki-selfhosted-supersedes-adr-0011.md`）を作成し、計画リポジトリへ ADR-0011 の Supersede と後継 ADR 起票を提案する。GitHub Issue 本文案も含める。
- **機能仕様 `docs/functional/FR-13_wiki-browsing.md`** を新設し、自前閲覧 API としての機能仕様を明文化。
- **運用仕様 `docs/operations/operations.md`** に WikiService（自前閲覧・Wiki.js 非配備）の運用注記を追加。
- **deploy 構成のコメント**（`deploy/docker-compose.yml` / `deploy/helm`）に「Wiki.js を意図的に配備しない」設計判断を明記し、監査時の誤検知を防ぐ。
- **コード側トレーサビリティ**: `WikiEndpoints.cs` のヘッダコメントに IADR-0013 を参照追加。

### 含まないもの
- 閲覧ロジック自体の実装（FR-13 の ABAC 適用は既存 PR #65 で完了済み。本 PR は挙動を変えない）。
- 計画リポジトリ（`project-planning`）への直接コミット（別リポ。feedback 経由で提案し人間が確定）。
- Wiki.js の配備（(a) を採らないため対象外）。

## 受け入れ基準

- [ ] IADR-0013 が作成され、決定・理由・トレードオフ・関連（ADR-0011 supersede 提案）を記載している。
- [ ] plan-feedback 記録が `feedback/` に作成され、category=`新たな制約(ADR要)`・related_ids・提案（Supersede）を含む。
- [ ] FR-13 機能仕様が自前閲覧 API の内容で作成されている。
- [ ] 運用仕様・deploy コメントが「Wiki.js 非配備は設計判断」である旨を明記している。
- [ ] `WikiEndpoints.cs` のコメントが IADR-0013 を参照している。
- [ ] 挙動変更が無い（既存テストがそのまま通る）。

## トレーサビリティ

起点 ID: FR-13, UC-07, ADR-0011, IADR-0009（親 Issue: #48 / 本 Issue: #56）
