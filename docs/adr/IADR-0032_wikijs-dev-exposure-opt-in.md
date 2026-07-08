---
title: IADR-0032 Wiki.js の dev ホスト公開は残し、本番系(Helm)の非公開を回帰ガードで保証する
type: impl-adr
status: Accepted
related_ids:
  - FR-13
  - UC-07
  - IADR-0020
  - IADR-0017
  - IADR-0026
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md"
---

# IADR-0032: Wiki.js の dev ホスト公開は残し、本番系(Helm)の非公開を回帰ガードで保証する

- 状態: Accepted
- 日付: 2026-07-08
- 決定者: claude（Issue #124 ／ #118 監査論点 2 の是正）

## 起点・関連

- 関連する計画書 ID: FR-13・UC-07・ADR-0011（Wiki 統合）
- 関連する実装 ADR: [IADR-0020](./IADR-0020_wiki-js-deployment-abac-gateway.md)（Wiki.js 配備・ABAC ゲートウェイ）・
  [IADR-0017](./IADR-0017_internal-service-auth-network-isolation.md)（ネットワーク分離）・
  [IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md)（存在秘匿）・
  [IADR-0026](./IADR-0026_mesh-mtls-supersedes-network-isolation.md)（mesh mTLS。§2「compose は BFF のみ host 公開」を本 IADR が dev 便宜の範囲で改定）

## コンテキストと課題

Wiki.js（閲覧・編集 UI の実体）の認可は、WikiService の **ABAC ゲートウェイ**が前段で強制する
（deny-by-default 属性フィルタ＋404 存在秘匿）。Wiki.js に**直接**到達できる経路は、この ABAC ゲートウェイを
**迂回**する。

- 従来 dev の compose は開発便宜で `wiki-js` を host 公開（`3001:3000`）していた。stg/prod（Helm）は
  `wikijs.ingress.enabled: false` で公開しておらず ClusterIP 限定だが、これは「**たまたま守られている**」状態で、
  構成変更で公開が混入しても検出する仕組みが無かった（#118 監査「逸脱の疑い 2」）。

## 決定

1. **dev の host 公開は残す。** `deploy/docker-compose.yml` の `wiki-js` は `ports: 3001:3000` を維持し、
   dev では管理 UI（OIDC 構成・ロケール導入・API キー発行）へ `http://localhost:3001` で直接アクセスできる。
   dev は開発ランタイムであり、ABAC の第一防御は本番系（mesh mTLS / ネットワーク分離）が担う。
2. **本番系（Helm）は公開しない。** `wikijs.ingress.enabled: false` を既定とし、Wiki.js は ClusterIP 限定
   （ゲートウェイ迂回の外部到達なし）。stg/prod では ABAC ゲートウェイ（WikiService）経由のみに限定する。
   - **IADR-0026 §2 の改定**: [IADR-0026](./IADR-0026_mesh-mtls-supersedes-network-isolation.md) §2 は
     「docker-compose では **BFF のみ** host 公開」と定めていたが、本 IADR はこれを **dev 便宜の範囲で改定**し、
     dev の compose に限り `wiki-js`(3001) の host 公開を許容する（フロントエンド SPA エッジ `frontend`(3100) も
     同様。IADR-0033）。**本番系の「内部サービスは Ingress 非公開」制約は不変**であり、その回帰ガードを (3) で強化する。
     根拠: dev は単一開発者のローカルランタイムで ABAC の第一防御は本番系が担うため、dev 便宜の host 公開は
     多層防御を実害なく緩めない。IADR-0026 側にも本改定を明記した。
3. **回帰ガードを追加する。** `NetworkIsolationTests` で
   (a) **本番系（Helm）の `wikijs.ingress.enabled` が `false`** であること（＝本番系構成では 3001 が公開されない）、
   (b) dev の 3001 公開は `wiki-js` に限定され他の内部アプリサービスへ波及していないこと
   （`InternalServices_MustNotPublishHostPorts` が引き続き保証）を検証する。
   これにより「本番系構成では 3001 が公開されない」ことを**機械的に**保証する。
   - **compose の profiles に関する補足**: docker compose のサービスレベル `profiles` は「サービスの起動有無」を
     制御するもので、**常時稼働サービスの個別ポート公開だけ**を条件化することはできない。Wiki.js は
     WikiService ゲートウェイの後段として dev でも常時稼働が必要なため、サービスごと profile 化すると
     プロファイル未指定時に Wiki 機能全体が停止してしまう。したがって dev/本番系の公開境界は
     「dev＝compose（3001 公開）／本番系＝Helm（Ingress 無効・回帰ガード）」という構成境界で表現する。

## 理由

- dev は開発利便（管理 UI への直接アクセス）を維持しつつ、ABAC 迂回の実害があるのは外部公開される本番系である。
- 本番系の非公開を**回帰ガードで機械的に保証**することで、「たまたま守られている」状態を「機械的に守られている」
  状態へ引き上げる（#118 監査論点 2 の是正）。

## 結果

- 良い影響: 本番系での ABAC 迂回経路が構成変更で混入しても CI（NetworkIsolationTests）が検出する。dev の利便は維持。
- トレードオフ: dev の compose では Wiki.js が host 公開されるため、dev ホストを共有する場合は 3001 への到達に留意する
  （dev は単一開発者のローカル前提。共有環境は本番系＝Helm を用いる）。

## 関連

- Amends: [IADR-0026](./IADR-0026_mesh-mtls-supersedes-network-isolation.md) §2（compose の「BFF のみ host 公開」を
  dev 便宜の範囲で改定。本番系制約は不変）。IADR-0020 の dev 公開方針を具体化。
- Superseded by: なし
