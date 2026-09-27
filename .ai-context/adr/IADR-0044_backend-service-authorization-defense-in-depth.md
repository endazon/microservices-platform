---
title: IADR-0044 バックエンドサービスの書き込み/管理APIへの認可強制（多層防御）
type: impl-adr
status: Accepted
related_ids:
  - FR-01
  - FR-06
  - FR-09
  - UC-03
  - UC-04
  - ADR-0004
  - IADR-0017
  - IADR-0039
  - IADR-0041
  - FR-19
  - ADR-0036
  - ADR-0056
  - ADR-0119
  - IADR-0277
  - IADR-0476
author: claude
created: 2026-07-09
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-09)
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md
---

# IADR-0044: バックエンドサービスの書き込み/管理APIへの認可強制（多層防御）

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-09（認可）／FR-01（データソース）／FR-06（文書）／UC-03／UC-04
- 関連 ADR: ADR-0004（Keycloak OIDC/JWT）／[IADR-0017](./IADR-0017_internal-service-auth-network-isolation.md)（ネットワーク分離＝第一防御）／[IADR-0039](./IADR-0039_datasource-management-bff-and-role-gating.md)（データソース管理の BFF ロールゲート）／[IADR-0041](./IADR-0041_document-write-bff-abac-scoped.md)（文書書き込みの BFF ABAC スコープ）／[IADR-0042](./IADR-0042_conversion-job-read-model.md)（変換ジョブ・ワーカー最小 HTTP）
- 関連仕様書: `docs/security/security.md`
- Issue: #174

## コンテキストと課題

Wave B の管理系画面（SC-05/06/07/09）は BFF 集約点でロール（admin もしくは admin/operator）を強制し、
権限外を 403/404 とした（[IADR-0039](./IADR-0039_datasource-management-bff-and-role-gating.md)／[IADR-0041](./IADR-0041_document-write-bff-abac-scoped.md)／[IADR-0042](./IADR-0042_conversion-job-read-model.md)）。しかし**後段サービス側の
一部エンドポイントは認可を課しておらず、BFF ゲートに依存**していた。

- `DataSourceService` `/datasources`（CRUD・sync）: 認可なし。
- `DocumentService` `/documents`（書き込み: POST/PUT/PATCH/publish/archive/DELETE）: 認可なし。

BFF を迂回してメッシュ内部から直接叩かれた場合、認可が効かない（多層防御の観点で弱い）。
mTLS/NetworkPolicy（[IADR-0017](./IADR-0017_internal-service-auth-network-isolation.md)／IADR-0026）は第一防御だが、アプリ層の実効境界を欠く。

## 決定

1. **各サービスの管理/書き込みエンドポイントに `RequireAuthorization`（ロール要件）を付与し、
   サービス単体でも実効境界とする**（BFF は利便性の集約点、サービスが最終防衛線）。ロール要件は
   BFF ゲートと一致させる（`platform-admin` もしくは `platform-operator`）。
   - `DataSourceService`: `/datasources` グループ全体に admin/operator を要求（[IADR-0039](./IADR-0039_datasource-management-bff-and-role-gating.md) の BFF ゲートと一致。
     データソースは運用資産で内部 HTTP 呼び出し元は BFF のみ）。
   - `DocumentService`: **書き込み**（POST/PUT/PATCH/metadata/publish/archive/DELETE）に admin/operator を要求
     （[IADR-0041](./IADR-0041_document-write-bff-abac-scoped.md) の BFF write ゲートと一致）。**読み取り**（GET 一覧/個別/版）は一般利用者が文書を
     閲覧するため据え置き（ロールで塞がない）。

     > **［2026-08-09 追記 / #629］書き込みのうち 5 口を管理者限定へ狭めた。**
     > 計画 §SC-05「管理系 3 画面の閲覧ロール」（裁定 Q19）が**破壊的操作は管理者限定**と定めており、
     > 本決定の admin/operator が計画より広かったためである。
     > **PUT / PATCH metadata / publish / archive / DELETE の 5 口へ `AdminOnly` を積んだ**
     > （グループ既定は閲覧の下限として残し、AND 合成で実効 admin のみ。[IADR-0128](./IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) 決定 1 の形）。
     >
     > **★ `POST /documents` だけは admin/operator のまま据え置いた。**
     > この口は人間の画面だけの口ではなく、**`ai-stock-trading` の KB 書き込み（AST/FR-08）が
     > BFF を経由せず直接叩いている**。その service-account は `platform-operator` しか持たない
     > （[IADR-0075](./IADR-0075_ast-kb-writer-service-client.md) が最小権限を理由に `platform-admin` の付与を明示的に却下している）ため、
     > 狭めると **AST の KB 書き込みが 403 で止まる**。計画の Q19 は**画面と人間のロール**の裁定であり
     > 機械クライアントを述べていないので、**実装側で決めずに計画へ裁定を依頼した**
     > （環流記録 20260809_document-write-machine-client.md（環流記録。計画リポ `projects/microservices-platform/10_feedback/20260809_document-write-machine-client.md` へ移設）。
     > 計画側へは PR planning#306 で伝達済み・**裁定待ち**）。
     > **人間に対する実効境界は BFF 側で閉じている**（`/bff/documents` の `POST` は `AdminOnly`）。

     > **［2026-09-27 追記 / #1629］上の 5 口（PUT / PATCH metadata / publish / archive / DELETE）は個人資料を対象外とする。
     > 主体を問わず 404。** ロールの門（本決定）は「誰が管理の口を使えるか」を決めるが、「どの文書に作用してよいか」を決めて
     > いなかった。そのため `AdminOnly` を持つ主体（人の管理者・機械の `abac-seeder`）は他人の個人資料を更新・公開・保管・削除でき、
     > 応答に完全な DTO（表題・owner・共有先）が返り、`PUT` の属性全置換で `owner` を自分へ書き換えることさえできた。
     > 計画 ADR-0036 D-08（管理者・運用者は平時、個人資料を一切閲覧できない）と ADR-0119 決定 3（個人資料は所有者と共有先にだけ返す。
     > 管理者を含む）に反する。BFF は `IsManageable` で同じ経路を塞いでいたが、本 IADR の前提（サービスが最終防衛線）のとおり
     > 後段でも塞ぐ。
     >
     > - **判定点は `DocumentManageScope.FindManageableAsync` ただ 1 つ**（5 口の取得がすべてここを通る）。不在と個人資料
     >   （`DocumentScopes.IsPrivateNote`。集合帰属）を同じ `null` に畳み、口は従前どおり 404 を返す（ADR-0056 決定 1・[IADR-0277](./IADR-0277_write-denial-returns-404.md)。
     >   403 は文書 ID の総当たりで実在を明かす）。入力検証（400）は従前どおり取得より前、`doc_scope` の不変性・並行制御は後ろ。
     > - **除外は所有者を問わず一律**（所有者が管理者でも 404）。BFF の `IsManageable` と同じ形であり、所有者の分岐を管理の口へ
     >   持ち込まない。個人資料を扱う経路は FR-19 の所有者の経路（`/private-notes/*`・Obsidian 同期・`PUT …/body`・共有台帳）だけで、
     >   所有権は `PrivateNote.OwnerId` と属性 `owner` の 2 か所にある。管理の口で所有者を許すと、属性全置換で両者を食い違わせる・
     >   ごみ箱を経ずに消す形が所有者自身にも開く。所有者の扱いを「404 か、所有者だけ許すか」で選べた（issue の「直すこと」）が、
     >   前者を採った。
     > - **タグの反映口（`POST /documents/{id}/tags`）の管理者の分岐も個人資料に及ばない**（[IADR-0364](./IADR-0364_tag-suggestion-reflection-and-dictionary-enforcement.md) 決定 3 の追記）。
     > - `owner`・`doc_scope` の書き換えの禁止（組織文書の側）は #1616（ADR-0119 決定 2）で扱う。本追記は個人資料を管理の口から外すだけである。
     > - 作業仕様書: `.ai-context/specs/20260927_issue-1629_admin-write-private-note-scope.md`。
2. **サービス間内部呼び出しは対象外**とする。`AuthorizationService` `/authz/scope`（ABAC スコープ照会）は
   RetrievalService/AiAnalysisService が内部呼び出しするため無認可を維持（[IADR-0017](./IADR-0017_internal-service-auth-network-isolation.md) と整合）。
   管理系 `/authz`（属性辞書・ポリシー）は既に AdminOnly。
3. **`ConversionService` `/jobs` は本 PR の対象外**とする。[IADR-0042](./IADR-0042_conversion-job-read-model.md) §決定3 で「ワーカーは最小 HTTP
   サーフェスに留め認可を課さない（ingress 非公開・[IADR-0017](./IADR-0017_internal-service-auth-network-isolation.md) で緩和）」と決定済みであり、これを
   覆すには ConversionService への認証基盤（`AddKnowledgePlatformAuth`）導入と ADR 更新を要する。本 PR は
   認証基盤を既に持つ DataSourceService/DocumentService の即応的ハードニングに絞る（follow-up に記録）。

## 根拠 / 代替案

- **BFF 集約点のロール要件をそのまま後段へ写す**: 認可の単一情報源を BFF に置きつつ、後段でも同一要件を
  二重化する（多層防御）。要件が乖離しないよう、後段はインラインの `RequireRole(AdminRole, OperatorRole)`
  で BFF と同一表現にする。
- **前段トークンの後段伝播が前提**: BFF は利用者の `Authorization` を後段へ伝播する（各 *BffEndpoints の
  `CreateForwardingClient`）。後段はこのトークンのロールを検証する。伝播が既存実装で確立済みのため、
  後段ゲート追加は BFF 正常系を壊さない（内部 HTTP 呼び出し元は BFF のみ・実測）。
- **DocumentService 読み取りを塞がない**: 文書閲覧は一般利用者の機能（SC-03）。読み取りに admin/operator を
  課すと正規の閲覧を破壊する。読み取りの機密制御は取得段（Retrieval）の ABAC（[IADR-0012](./IADR-0012_retrieval-search-fail-closed-scope.md)）が担う。
- **属性 ABAC スコープの厳密検証（[IADR-0041](./IADR-0041_document-write-bff-abac-scoped.md) で見送り）は本 PR で扱わない**: 文書作成時に付与属性が
  呼び出し者の ABAC スコープ内かを DocumentService が AuthorizationService へ問い合わせて検証する強化は、
  サービス間呼び出し・失敗時の扱いを伴い独立性が高い。follow-up とする。

## 影響

- `DataSourceService`: `MapDataSourceEndpoints` のグループに `RequireAuthorization`。
- `DocumentService`: 書き込みを別グループへ分離し `RequireAuthorization`。読み取りは据え置き。
- テスト: 両サービスに `TestAuthHandler`（既定 admin・`X-Test-Roles` で上書き）を追加し、既存
  エンドポイントテストを認証下で通す。権限外（403）の否定テストを追加。

## フォローアップ

- `ConversionService` `/jobs` への認可（認証基盤導入＋[IADR-0042](./IADR-0042_conversion-job-read-model.md) §決定3 の更新）。
- 文書作成時の付与属性が呼び出し者 ABAC スコープ内かの厳密検証（[IADR-0041](./IADR-0041_document-write-bff-abac-scoped.md) 見送り分）。
- 認可の単一情報源化（共有ポリシー定数化）と、mTLS/NetworkPolicy（IADR-0026）との役割整理。
