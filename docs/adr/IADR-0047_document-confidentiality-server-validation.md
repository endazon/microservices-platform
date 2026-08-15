---
title: IADR-0047 文書の必須属性（機密区分）のサーバー側検証
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - FR-06
  - UC-03
  - SC-05
  - IADR-0044
  - IADR-0041
  - IADR-0019
author: claude
created: 2026-07-10
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/03_usecases (UC-03 例外フロー: 必須属性未設定は保存拒否)"
  - "../../planning/projects/microservices-platform/05_screens (SC-05: 機密区分は必須)"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-05/FR-06)"
---

# IADR-0047: 文書の必須属性（機密区分）のサーバー側検証

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-06（文書 CRUD・版管理）／FR-05（ABAC 属性）／UC-03（例外フロー）／SC-05（画面）
- 関連 ADR: [[IADR-0044]]（多層防御・サービス最終防衛線）／[[IADR-0041]]（BFF ABAC スコープ・付与属性の
  厳密検証見送り）／[[IADR-0019]]（データソース既定属性のフェイルセーフ）／ADR-0004（Keycloak）
- 関連仕様書: `docs/specs/20260710_issue-199_document-confidentiality-server-validation.md`、
  `docs/tests/FR-06_document-crud-versioning.md`、`docs/security/security.md`
- Issue: #199

## コンテキストと課題

UC-03 例外フローと SC-05 は「機密区分（`confidentiality`）は必須。未設定なら保存拒否」を定める。しかし
この必須検証は**フロントエンドの select 既定値（`internal`）に依存**しており、サーバー側（BFF/
DocumentService）は `attributes` 未指定・`confidentiality` 欠落のリクエストを 201 で受理していた。
admin/operator ロールを持つ API 直叩き・別クライアントからは機密区分の無い文書を作成できた。

下流は fail-closed（[[IADR-0012]] 検索除外・[[IADR-0021]] `isPrivate=true`）で漏えい方向には倒れないが、
計画の受け入れ条件「保存拒否」を満たさず、属性欠落文書は検索にも Wiki にも出ないため「保存できたのに
見えない」運用混乱を招く。[[IADR-0044]] の多層防御方針（サービスが最終防衛線）に整合させ、必須検証を
サーバー側で強制する必要がある。

## 決定

1. **DocumentService の手動書き込み経路で機密区分を必須検証する。**
   `POST /documents`・`PUT /documents/{id}`・`PATCH /documents/{id}/metadata` の各エンドポイントで、
   `attributes.confidentiality` の欠落（未指定・空）・未知値を **400（ValidationProblem）** で拒否する。
   検証ロジックは単一情報源 `DocumentAttributes.ValidateConfidentiality` に置く。

2. **正準値集合は静的定数で持ち、動的な属性辞書照合は行わない（本 PR では）。**
   許容値は `public` / `internal` / `confidential` / `restricted`（FR-05・AuthorizationService の
   `AttributeDefinition.AllowedValues` と一致）。DocumentService から AuthorizationService へ問い合わせて
   動的に許容値・必須性を検証する強化は、サービス間呼び出しと失敗時挙動（fail-open/closed）の設計を伴い
   独立性が高いため、[[IADR-0041]] で見送った「付与属性が呼び出し者スコープ内かの厳密検証」と合わせて
   follow-up とする。値集合は FR-05 で安定しており、静的集合で計画の受け入れ条件を満たせる。

3. **取り込み（パイプライン）経路は本 PR では変更しない。**
   `Document.CreateNormalized` / `ApplyNormalized`（イベント駆動の正規化取り込み）には検証を課さない。
   データソース既定属性（[[IADR-0019]]）で `confidentiality` が付与される設計であり、ここで 400 を投げると
   取り込みイベントを落とす。取り込み経路のフェイルセーフ既定補完は follow-up とする。

4. **既存の属性欠落文書は「修正要求」方式で移行する。**
   既存レコードは保持する（下流 fail-closed で漏えいはしない）。`PUT`/`PATCH` も必須検証対象のため、
   属性欠落文書は正しい `confidentiality` を付与しない限り更新できない（次回編集時に補正を要求）。
   [[IADR-0019]] のフェイルセーフ既定（`internal`）に揃えた一括バックフィルは任意の ops follow-up とし、
   本 PR には含めない。
   - **注意（欠落だけでなく非正準値も同じ「詰み」になる）**: 取り込み経路（`CreateNormalized`/`ApplyNormalized`）は
     本 PR の検証対象外のため、データソース既定属性（[[IADR-0019]] `DataSource.WithConfidentialityFailsafe`。
     **#516 で `WithRequiredAttributeFailsafe` へ改称し `owner` / `department` を追加した。[[IADR-0199]]**）に
     管理者が非正準値（誤字・別ケース、例 `"Confidential"`）を設定すると、取り込みでそのまま永続化され得る。
     この文書はその後の手動 `PUT`/`PATCH` が 400 で通らなくなる（＝欠落文書と同じ「修正要求」の袋小路）。
     取り込み側の値正準性チェックは follow-up（下記）で扱う。

5. **正準値の比較は大文字小文字を区別する（`StringComparer.Ordinal`）。**
   ABAC のスコープ照合・検索フィルタは正準の小文字値（`internal` 等）を前提とするため、DocumentService は
   非正準ケース（`"Internal"` 等）を 400 で拒否し、格納値を正準小文字に強制する（`"Internal"` が保存されると
   下流のフィルタ一致が壊れる）。
   - **既知の不一致（follow-up）**: 属性辞書を管理する `AuthorizationService.AbacValidation` は
     `AllowedValues.Contains(v, StringComparer.OrdinalIgnoreCase)` と**大文字小文字を無視**して比較しており、
     同じ `confidentiality` 属性の正準性定義が両サービスで食い違う。DocumentService の方が厳格（Ordinal）で
     漏えい方向には安全だが、比較ポリシーの整合は動的辞書照合の follow-up と合わせて解消する。

## 根拠 / 代替案

- **検証点をサービス側（DocumentService）に置く**: BFF/フロントは利便性の早期検証点であり、最終防衛線は
  サービス（[[IADR-0044]]）。BFF 迂回の直接呼び出しを塞ぐには、正本のサービス側で検証する必要がある。
  BFF は後段 400 を `RelayAsync` で内容非依存に透過するため、BFF 側の追加実装は不要（既存の
  `Create_WhenTitleMissing_Passes400Through` が透過を担保）。
- **ドメイン例外ではなくエンドポイント 400 を採用**: 既存のタイトル必須検証（`ValidationProblem`）と同じ
  表現に揃え、計画の「400 を返す」受け入れ条件に直接対応する。ドメイン `Create` は取り込み経路と共有する
  ため、そこに例外を仕込むと取り込みを巻き込む。検証は手動書き込みエンドポイントに限定する。
- **静的集合 vs 動的辞書**: 動的辞書照合は正確だが、書き込みごとに AuthorizationService への往復を足し
  （[[IADR-0045]]／#179 の往復削減方針と逆行）、辞書未整備時の縮退設計も要る。FR-05 の値集合は安定して
  いるため、静的集合で「保存拒否」を満たしつつ結合と往復を増やさない。

## 影響

- `DocumentService.Api`: `Foundation/Domain/DocumentAttributes.cs`（新規・検証ヘルパー＋正準集合）、
  `Foundation/Endpoints/DocumentEndpoints.cs`（POST/PUT/PATCH に検証を追加）。
- テスト: `DocumentAttributesTests`（ヘルパー単体）、`DocumentConfidentialityValidationTests`
  （エンドポイント・欠落/未知値 400・正準値 201/200）。既存フィクスチャに `confidentiality` を補完。
- 回帰なし: 読み取り（GET）・取り込み経路・版管理・認可（[[IADR-0044]]）は不変。

## フォローアップ

- DocumentService → AuthorizationService の動的属性辞書照合（必須性・許容値・付与属性のスコープ内検証。
  [[IADR-0041]] 見送り分と統合）。あわせて**比較ポリシーの整合**（DocumentService=Ordinal /
  AuthorizationService=OrdinalIgnoreCase の不一致解消。決定 5 参照）。
- 取り込み経路のフェイルセーフ既定補完＋**値の正準性チェック**（[[IADR-0019]] 既定属性と整合。
  非正準値の混入を取り込み段で弾く。決定 4 の注意参照）。
- 既存属性欠落・非正準値文書の一括バックフィル（ops、任意）。
- セキュリティ仕様書への反映（サーバー側の機密区分必須検証を防御層として記載）は #201（PR #214）の
  `docs/security/security.md` データ保護表で実施済み（本 PR では security.md を変更しない）。
