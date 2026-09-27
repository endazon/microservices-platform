---
title: IADR-0075 AST の KB 書き込みは microservices-platform レルムの専用 confidential client（service-account に platform-operator）で受ける
type: impl-adr
status: Accepted
related_ids:
  - FR-06
  - IADR-0030
  - IADR-0041
  - IADR-0044
  - ADR-0018
  - ADR-0119
  - IADR-0475
author: claude
created: 2026-07-19
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
  - planning:projects/microservices-platform/06_technical/10_composability-design.md
---

# IADR-0075: AST の KB 書き込み用サービスクライアント（platform-operator）を realm に追加する

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: claude（実装）／ endazon（マージ判断）

## 起点・関連

- 関連する計画書 ID: **FR-06**（文書管理＝ナレッジベースへの保存）、ADR-0018（合成可能アーキテクチャ・可変ユニットの拡張は基盤無改修）
- 関連 ADR: [IADR-0030](./IADR-0030_operator-role-and-config-viewer-policy.md)（platform-operator ロール新設）／[IADR-0041](./IADR-0041_document-write-bff-abac-scoped.md)（文書書き込みの BFF ABAC ゲート）／
  [IADR-0044](./IADR-0044_backend-service-authorization-defense-in-depth.md)（バックエンド書き込みの多層防御＝`POST /documents` は platform-admin/operator 必須）
- Issue: AST endazon/ai-stock-trading#18（AST/FR-08 ナレッジベース保存の実 s2s 配線）。AST 側は
  `feat/FR-08-kb-writer-cross-realm-s2s`（AST/IADR-0093）。
- 対応する AST 側決定: AST `docs/adr/IADR-0093`（KB の s2s を本レルムの専用クライアントでクロスレルム認証する）。

## 背景・課題

`ai-stock-trading`（AST）ユニットは、確定報告書・収集情報を DocumentService（`POST /documents`）へ保存する（AST/FR-08）。
しかし本エンドポイントは [IADR-0044](./IADR-0044_backend-service-authorization-defense-in-depth.md) の多層防御で `platform-admin`/`platform-operator` ロール必須であり、かつ
DocumentService は **`microservices-platform` レルム**の Authority で JWT を検証する。AST の既存 s2s クライアント
（AST レルムの `ai-stock-trading-svc` / `trading-service`）が発行するトークンは:

- issuer が **AST レルム**のため、本レルムの Authority 検証を通らない（401）。
- 仮に通っても `trading-service` は `platform-admin`/`platform-operator` に該当しない（403）。

よって AST は本レルムのクライアント資格を持たない限り KB へ書き込めない。基盤無改修（ADR-0018）の下で AST に
書き込み経路を与えるには、**本レルム側に AST 用のサービスクライアントを用意する**のが妥当（ロールの付与主体は
基盤＝本レルムが握る）。

## 決定

`deploy/keycloak/microservices-platform-realm.json` に、AST の KB 書き込み専用の機密クライアントを追加する:

- **client `ai-stock-trading-kb-writer`**（`publicClient: false`・`serviceAccountsEnabled: true`・
  `standardFlowEnabled: false`・`directAccessGrantsEnabled: false`）＝ client_credentials のみ。
- **service-account に realm role `platform-operator` を付与**（`service-account-ai-stock-trading-kb-writer` ユーザ）。
  これで本クライアントの client_credentials トークンは `POST /documents` の書き込みゲート（[IADR-0044](./IADR-0044_backend-service-authorization-defense-in-depth.md)）を通過する。
- dev export のシークレットはプレースホルダ。本番シークレットは config（Vault/Secrets）から供給し、AST 側は
  k8s Secret として受け取る（値は Git に載せない）。

### 権限の最小化

- 付与は `platform-operator` のみ（`platform-admin` は付与しない）。書き込み（作成・更新・メタデータ）に必要な最小権限で、
  ABAC 管理（AdminOnly）等の管理系は与えない（[IADR-0030](./IADR-0030_operator-role-and-config-viewer-policy.md) の operator 定義に沿う）。
- 既存クライアント（wiki-js/bff/spa-web）や既存ロールには一切触れない。追加のみ。

## 影響・リスク

- realm import の追加のみ。既存の認証・認可・ユーザーへの影響なし。読み取り（一般利用者の文書閲覧）にも影響しない。
- 本クライアントのシークレットが AST 環境へ配布されるまでは何も起きない（AST 側は Secret 空なら no-op）。
- 削除（アーカイブ/公開）まで含む write グループを通過できる点は operator 権限どおり。トークンは BFF を経由しないため
  [IADR-0041](./IADR-0041_document-write-bff-abac-scoped.md) の ABAC スコープ・ナローイング（呼び出し者スコープ内限定）は効かず、効くのは [IADR-0044](./IADR-0044_backend-service-authorization-defense-in-depth.md) のロール
  チェック（admin/operator）のみである。ただし **AST 側のクライアント実装（`HttpKnowledgeBaseWriter`）は構造上
  `POST /documents`（カタログ登録＝作成）しか発行しない**（update/publish/archive/delete を呼ぶコード経路を持たない）。
  よって実運用で到達するのは作成のみで、破壊的操作は AST の利用範囲に現れない。より細粒度の「作成専用」スコープを
  与えるには専用ロール/ポリシーの新設が要るが、消費実装が作成限定であること・[IADR-0044](./IADR-0044_backend-service-authorization-defense-in-depth.md) のロールゲートが効くこと
  から、本 PR では operator 付与に留める（過剰なロール新設を避ける・検討した代替案 C）。

> **［2026-09-27 追記 / #1616］上の「AST 側のクライアント実装は構造上 `POST /documents` しか発行しない」は崩れている**
> （計画 ADR-0119 §実測 7）。AST（`origin/develop` `892376e8`）の KB 用アダプタは `POST /documents` に加えて
> `GET /documents`（KB の入れ直しで既存の写しを探す一覧。`HttpKnowledgeDocumentCatalog`）と `PUT /documents/{id}/body`
> （本文の投入。所有者の束縛で通る）を呼ぶ。submodule の pin `7a7a8a14` は `POST` だけだが、pin は前進する。
>
> あわせて、計画 ADR-0119 決定 2 により **機械クライアントは自分が `owner` の組織文書に限りメタデータ更新（`PATCH /documents/{id}/metadata`）と
> 削除（`DELETE /documents/{id}`）を行える**ようになった（ロールは足さない。本クライアントの `platform-operator` のまま）。
> 本クライアントが作る文書の `owner` は `service-account-ai-stock-trading-kb-writer` であり、**AST が触れられる破壊的操作は
> 自分が作った組織文書の更新・削除に限られる**（他の主体の文書は 403 / 404、個人資料は 404、`owner`・`doc_scope` は書き換え不可。
> 編集・公開・アーカイブは管理者限定のまま）。上の「破壊的操作は AST の利用範囲に現れない」は、この範囲へ改める。
> 専用ロールを新設しない判断（代替案 C の却下）は ADR-0119 が同じ理由で維持した。実装は [IADR-0044](./IADR-0044_backend-service-authorization-defense-in-depth.md)
> 決定 1 の 2026-09-27 追記（#1616）。

## 検討した代替案

- **A: DocumentService の write ロール要件を緩めて AST レルムのトークンを通す** — 却下。[IADR-0044](./IADR-0044_backend-service-authorization-defense-in-depth.md) の多層防御を弱める。
- **B: AST レルムに platform-operator 相当を作る** — 却下。issuer が AST レルムのままで本レルムの Authority 検証を通らない。
- **C: 専用ロールを新設して write グループへ足す** — 却下（過剰）。既存の `platform-operator` が write 要件を満たすため、
  ロールを増やさず最小の追加（サービスクライアント1つ）で足りる。

## 事後対応（PR #317 / Issue #18）

- 本クライアント追加時（[#307](https://github.com/endazon/microservices-platform/pull/307)）の `description` が 364 文字あり、
  Keycloak の `CLIENT.DESCRIPTION`（`varchar(255)`）を超過して realm import が **SQLSTATE 22001** で失敗し pod がクラッシュした。
  意味を保ったまま 251 文字へ短縮して是正した（権限・ロール・フロー等の挙動は不変・description の長さのみ）。
- 再発防止として `scripts/check-realm-constraints.js` を追加し、CI（`ci.yml` の `realm-constraints` ジョブ）で realm export の
  文字列フィールド長を `varchar(255)` に対して機械検査する。対象は clients/clientScopes/protocolMappers/roles/groups の name・
  description 等（オーバーフローしやすい自由記述/名称に絞った軽い lint）。対象外フィールドで同種の失敗が起きた場合は
  `collectFields` に対象を足して範囲を広げる方針とする。
