---
title: 作業仕様書 — 基盤（platform-infra）との通信を TLS にする 段 1（postgres・rabbitmq のサーバ側 TLS と、基盤のサービスの検証つきの接続。GHSA-j4qv-2w99-xg5p）
type: spec
status: in-progress
related_ids: [NFR-16, NFR-18, ADR-0005, ADR-0023, ADR-0047, IADR-0591, IADR-0206, IADR-0573, IADR-0577, IADR-0589, IADR-0590]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-16 通信暗号化: 恒久はサービス間 mTLS・外部通信 TLS）
  - planning:projects/microservices-platform/07_adr/ADR-0005（サービス間通信を mTLS で徹底する）
  - planning:projects/microservices-platform/07_adr/ADR-0023（証明書の配布層は cert-manager。CA 固有の設定は ClusterIssuer に閉じる）
  - planning:projects/microservices-platform/07_adr/ADR-0047（経路B の証明書は selfsigned CA を許容する）
issue: "GHSA-j4qv-2w99-xg5p"
---

# 作業仕様書 — 基盤との通信を TLS にする 段 1（GHSA-j4qv-2w99-xg5p）

> 本仕様書は実装着手前に作成した（着手 2026-10-23）。判断の記録は **IADR-0591**（新規）に置く。
> 多層防御の強化（基盤との通信を暗号化し、サーバ証明書を検証する）。参照は GHSA の ID だけとする。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: **NFR-16**（通信暗号化。恒久はサービス間 mTLS・外部通信 TLS）・**NFR-18**（同じ GHSA の他の変更と同じ起点）。
- 計画 ADR: **ADR-0005**（Accepted。サービス間通信の暗号化を徹底する）・**ADR-0023**（Accepted。証明書の配布層は cert-manager、CA 固有の設定は ClusterIssuer に閉じる）・**ADR-0047**（経路B の selfsigned CA）。
  計画との矛盾は無い。メッシュの mTLS は MSP の名前空間の受信にだけ効き、別の名前空間（platform-infra）の基盤への送信は auto-mTLS の対象外である（IADR-0590 の表）。本変更はその区間をアプリ側の TLS で暗号化し、計画の「通信の暗号化の徹底」へ寄せる。
- 実装 ADR: **IADR-0591**（新設）。前提: IADR-0206（selfSigned → CA の 2 段・cert-manager の導入）・IADR-0573 / IADR-0577（ブローカの到達の制限・サービスごとのユーザー）・IADR-0589（egress の既定拒否）・IADR-0590（メッシュの mTLS は名前空間の中だけ）。

## 範囲と段

先行の段（同じ GHSA）: egress の既定拒否（IADR-0589）・メッシュの mTLS の既定 STRICT（IADR-0590。段 4a）。本 PR は段 4b の**最初の段**である。

| 段 | 内容 | 本 PR |
| --- | --- | --- |
| 4b-1 | postgres・rabbitmq をサーバ側 TLS にする（平文と併存）。基盤（MSP）のサービスは検証つきの TLS で繋ぐ。CA を AST の名前空間にも置く | ○ |
| 4b-2 | 取引ユニット（AST）の接続を TLS へ切り替える（AST の chart と、MSP の compose にある AST の 3 サービス）。基盤の他のクライアント（Keycloak の DB・Wiki.js・バックアップの CronJob）も切り替える | 後続（AST は AST リポジトリの PR） |
| 4b-3 | 平文を閉じる（postgres は `hostssl` だけ・`hostnossl` は拒否、rabbitmq は `listeners.tcp = none`、`rabbitmq-ingress-clients` から 5672 を外す） | 後続（4b-2 の切り替えを確かめた後） |
| 4b-4 | valkey・qdrant・otel-collector・keycloak（HTTP 8080）の TLS | 後続 |

範囲外: AST の配備スクリプト・chart（別リポジトリ）。本番像の CA の供給元（配備の側が ConfigMap を用意する。下の未決）。

## 母集合（追随の引き方。規則 9・10）

- 接続文字列: `git grep -n -E "Host=postgres|amqp://|ConnectionStrings__|RabbitMq__ConnectionString" -- deploy scripts src/platform src/knowledge`。
  - helm の `templates/deployment.yaml`（`global.db` / `global.messaging`）1 か所。values の `services.*.database`（10）・`messaging: true`（7）が描画の対象。
  - compose: `x-db-env` / `x-rabbit-env` と各サービスの個別の接続文字列（MSP 10・AST 3）。
  - 経路B の基盤の他のクライアント: Keycloak（`KC_DB`）・Wiki.js（`templates/wikijs.yaml` の `DB_HOST`）・バックアップの CronJob（`PGHOST`）。**段 1 では変えない**（併存の平文で繋がる）。
- ブローカの呼び出し: `git grep -n "UseRabbitMq" -- src/knowledge/backend/Services` で 7 サービスの `Program.cs`（試験・統合試験の器は平文の Testcontainers なので対象外）。
- 到達の制限: `deploy/local/infra/rabbitmq.yaml` の `rabbitmq-ingress-clients`（5672）・`deploy/local/values-local.yaml` の egress の宛先（5432 / 5672）。
- AST: `src/ai-stock-trading` は submodule（本 worktree では未取得）。AST リポジトリの develop を読み取りで確認した（`deploy/helm/ai-stock-trading/templates/deployment.yaml` が `Host=postgres;Port=5432` と `amqp://…@rabbitmq:5672` を描く。AST も WolverineFx.RabbitMQ 6.24.5 と Npgsql の EF Core 10）。

## 方式

1. **証明書**: cert-manager（IADR-0206 で導入済みの版・digest）。基盤専用の selfSigned → CA の 2 段（`platform-infra-root-ca` / ClusterIssuer `platform-infra-ca`）。葉証明書 `postgres-tls` / `rabbitmq-tls`（platform-infra。名前は素の名前と FQDN の 4 形。ECDSA P-256・1 年）。エッジの CA とは分ける。
2. **起動器**: cert-manager の導入を LOCALEDGE の段から関数へ出し、`[4/7]` の先頭で呼ぶ（2 回目以降は何もしない）。`deploy/local/infra-tls` を当てて証明書の Ready を待ってから基盤を当てる。`[5/7]` で CA（ca.crt だけ）を ConfigMap `platform-infra-ca` として MSP と AST の名前空間へ写す。読めなければ止める。
3. **サーバ**: postgres は `ssl=on`（TLS 1.2 以上）。鍵は init コンテナで uid 70・0600 へ写す。rabbitmq は 5671 に TLS の受け口（TLS 1.3 / 1.2）を足す。5432 / 5672 の平文は段 1 では残す。
4. **基盤のサービス（helm）**: DB は `SSL Mode=VerifyFull;Root Certificate=/etc/platform-infra-ca/ca.crt`。ブローカは `amqps://…:5671` と `SSL_CERT_DIR=/etc/ssl/certs:/etc/platform-infra-ca`。CA は ConfigMap を非 optional でマウントする。`global.db.tls.enabled` / `global.messaging.tls.enabled`（既定 true）は切り戻し専用で、検証を緩める受け口は持たない。
5. **ブローカの名前の照合**: RabbitMQ.Client は amqps の URI で名前の不一致を受け入れる既定を持つ（実測）。共通ヘルパ `UsePlatformRabbitMq` で「受け入れてよい誤り」を空にし、7 サービスの呼び出しを置き換える。
6. **到達の制限**: `rabbitmq-ingress-clients` に 5671 を足す（5672 は AST のために残す）。経路B の egress の宛先（MSP）は 5671 だけにする。
7. **compose**: cert-manager が無いので、同じ形の CA と葉証明書を 1 回きりのコンテナ（ブローカのイメージの openssl）で作る。サーバの設定は経路B と同じ。基盤のサービスは検証つきで繋ぎ、AST の 3 サービスは平文のまま（`x-rabbit-env-ast-plain`）。
8. **稼働の効き**: 統合スタックに門を足す（MSP の利用者の DB・ブローカの接続がすべて TLS、サーバ証明書を CA と名前で検証できる、別の名前では検証に失敗する）。

## 受け入れ基準（試験への写像）

- [x] 既定の起動で cert-manager → infra-tls → 証明書 Ready → 基盤の apply → CA の写し（MSP・AST）→ helm の順に進む。cert-manager は 1 回だけ当てる（`k8s-local-up.test.js`）。
- [x] ルート CA を読めなければ CA の写しも helm も行わずに止まる（`k8s-local-up.test.js`）。
- [x] 証明書の名前・発行元・Secret の非 optional のマウント・postgres の引数・rabbitmq の TLS 設定と Service / NetworkPolicy の 5671（`k8s-local-up.test.js`）。
- [x] 既定・values-local の描画で、DB へ繋ぐ全 Deployment が VerifyFull ＋ 基盤の CA、ブローカへ繋ぐ全 Deployment が amqps:5671 ＋ 信頼ストアへの追加、CA は非 optional で 1 キーだけマウント。描画で止まる条件・切り戻しの形・変異（`helm-infra-tls.test.js`）。
- [x] ブローカのサービスごとのユーザーの試験が amqps でも成り立つ（`helm-broker-per-service-users.test.js`）。経路B の egress はブローカの 5671 だけを許し 5672 を拒む（`helm-egress-network-policy.test.js`）。
- [x] `UsePlatformRabbitMq` は amqps で受け入れてよい誤りを空にし、名前を照合する。前提として素の `UseRabbitMq(Uri)` は名前の不一致を受け入れる。変異（行を外す）で赤（`WolverineExtensionsTests`）。
- [x] compose の TLS 設定は経路B と同じ内容。基盤のクライアントは VerifyFull ＋ CA のマウント、AST の 3 サービスは平文。配備の宣言に検証を緩める形が無い。サービスは素の `UseRabbitMq` を呼ばない（`scripts.repo.test.js`）。
- [x] compose の基盤（postgres・rabbitmq・証明書の生成）を手元の docker で起こし、検証つきの接続が通る・別の CA と別の名前では通らない・平文も併存する、を実測（IADR-0591 §実測）。
- [ ] 統合スタックを PR のブランチで `ISTIO=1` で流し、新しい門と既存の門が緑。

## 影響範囲

- `deploy/local/infra-tls/`（新規）・`deploy/local/infra/{postgres,rabbitmq}.yaml`・`deploy/local/values-local.yaml`・`deploy/helm/microservices-platform/{values.yaml,templates/deployment.yaml}`・`deploy/docker-compose.yml`・`deploy/rabbitmq/20-tls.conf`（新規）・`scripts/k8s-local-up.sh`・試験・`.github/workflows/{ci,integration-stack}.yml`・`src/platform/backend/Shared/Platform.Shared.Infrastructure`（共通ヘルパ）・`src/knowledge/backend/Services/*/Program.cs`（7）・文書。
- 稼働のクラスタ: 再実行で cert-manager が入り、postgres と rabbitmq が作り直される（ブローカは永続化していないので、キューは作り直しで空から立つ。起動の直後にサービスごとのユーザーは復元される）。基盤のサービスは TLS で繋ぎ直す。AST の接続は変わらない。
