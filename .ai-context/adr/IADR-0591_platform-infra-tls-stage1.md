---
title: IADR-0591 基盤（platform-infra）との通信を TLS にする 段 1（postgres・rabbitmq は cert-manager の基盤専用 CA の証明書でサーバ側 TLS を受け、平文と併存させる。基盤のサービスは VerifyFull / amqps で CA と名前を検証して繋ぎ、取引ユニットは後の段で切り替え、平文はその後に閉じる）
type: impl-adr
status: Accepted
related_ids: [NFR-16, NFR-18, ADR-0005, ADR-0023, ADR-0047, IADR-0206, IADR-0573, IADR-0577, IADR-0589, IADR-0590]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-16 通信暗号化）
  - planning:projects/microservices-platform/07_adr/ADR-0005（サービス間通信を mTLS で徹底する）
  - planning:projects/microservices-platform/07_adr/ADR-0023（証明書の配布層は cert-manager。CA 固有の設定は ClusterIssuer に閉じる）
  - planning:projects/microservices-platform/07_adr/ADR-0047（経路B の selfsigned CA）
related_specs:
  - ../specs/20261023_j4qv_infra-tls-stage1.md
---

# IADR-0591: 基盤との通信を TLS にする 段 1（GHSA-j4qv-2w99-xg5p）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（形の選択。多層防御の強化の一環）

## 起点・関連

- 起点: GHSA-j4qv-2w99-xg5p（多層防御の強化）。
- 計画: **NFR-16**（恒久はサービス間 mTLS・外部通信 TLS）・**ADR-0005**（通信の暗号化の徹底）・**ADR-0023**（証明書の配布層は cert-manager。CA 固有の設定は ClusterIssuer に閉じる）・**ADR-0047**（経路B の selfsigned CA）。要求 **NFR-18**（同じ GHSA の他の変更と同じ起点）。
- 前提: [IADR-0206](./IADR-0206_local-edge-tls-cert-manager.md)（cert-manager・selfSigned → CA の 2 段）・[IADR-0573](./IADR-0573_rabbitmq-per-service-users-and-ingress-policy.md) / [IADR-0577](./IADR-0577_rabbitmq-msp-per-service-users-and-default-user-disable.md)（ブローカの到達の制限・サービスごとのユーザー）・[IADR-0589](./IADR-0589_egress-default-deny-network-policy.md)（egress の既定拒否）・[IADR-0590](./IADR-0590_mesh-mtls-strict-by-default.md)（メッシュの mTLS は MSP の名前空間の受信にだけ効く。基盤への送信は auto-mTLS の対象外。同 IADR の結果 3 が本 IADR の範囲）。

## コンテキストと課題

基盤（platform-infra）の postgres・rabbitmq・valkey・qdrant・otel-collector・keycloak は MSP と別の名前空間に居て、サイドカーを持たない。MSP の Pod から基盤への通信はメッシュの mTLS の対象にならず、アプリの接続文字列の形（平文）がそのまま通信の形になる。取引ユニット（AST）も同じ postgres・rabbitmq を使う。

制約:

- 証明書の仕組みはリポジトリにある cert-manager（IADR-0206）に合わせる。ただし cert-manager はこれまで `LOCALEDGE=1` の opt-in の段だけが入れていた。
- 検証を緩める接続（DB の `Require` / `Trust Server Certificate`、名前の照合を外す等）は使わない。
- 経路B（`deploy/local`・起動器）、helm の本番像、compose の 3 つで形を揃える。
- AST の接続を MSP の変更だけで壊さない。

## 検討した選択肢

1. **一度に平文を閉じ、MSP と AST を同時に切り替える** — AST の chart は別リポジトリで、MSP の PR だけでは AST の接続が止まる。採らない。
2. **サーバを平文と TLS の併存にし、MSP を先に切り替え、AST の切り替えの後で平文を閉じる（採用）** — 各段で何も止まらない。併存の間は平文の口が残る（段 4b-3 で閉じる）。
3. **CA をエッジの CA（local-edge-root-ca）と共用する** — 基盤のクライアントがエッジの葉証明書まで信頼する。発行の範囲を分けられない。採らない。
4. **CA を起動器が openssl で作る** — 既存の仕組み（cert-manager）と二重になり、更新・差し替え（ClusterIssuer の issuerRef）の流儀から外れる。経路B では採らない（compose だけは cert-manager が無いので同じ形を openssl で作る。決定 7）。
5. **ブローカの CA を `SSL_CERT_FILE` で渡す** — 信頼ストアを**置き換える**ので、外部の公開 CA の検証が消える。`SSL_CERT_DIR` に既定のストアを先に残して足す形にする（.NET 10 は複数のディレクトリを読み、ファイル名がハッシュでなくても読む。実測）。

## 決定

1. **証明書は cert-manager の基盤専用の 2 段の CA で発行する。** ClusterIssuer `platform-infra-selfsigned` → ルート CA `cert-manager/platform-infra-root-ca` → ClusterIssuer `platform-infra-ca` → 葉証明書 `platform-infra/postgres-tls`・`rabbitmq-tls`（名前は素の名前・`<名>.platform-infra`・`.svc`・`.svc.cluster.local`。ECDSA P-256・1 年）。宣言は `deploy/local/infra-tls/`（親の kustomization に含めない。CRD の後に当てる）。
2. **cert-manager は既定で入れる。** 導入を関数（`install_cert_manager`）へ出し、起動器の `[4/7]` の先頭で呼ぶ。LOCALEDGE の段は同じ関数を呼び、2 回目は何もしない。証明書の Ready を待ってから基盤を当てる（postgres・rabbitmq は証明書の Secret を非 optional でマウントする）。当てられなければ止める。
3. **サーバは段 1 では平文と併存させる。** postgres は `ssl=on`・TLS 1.2 以上（`pg_hba` は既定の `host` のまま）。鍵は init コンテナで postgres の利用者（uid 70）の 0600 へ写す（fsGroup はデータのボリュームの権限まで変えるので使わない）。rabbitmq は 5671 に TLS の受け口（TLS 1.3 / 1.2・クライアント証明書は求めない）を足し、5672 を残す。
4. **基盤のサービスは CA と名前を検証して繋ぐ。** helm は DB へ `SSL Mode=VerifyFull;Root Certificate=/etc/platform-infra-ca/ca.crt`、ブローカへ `amqps://…@rabbitmq:5671` と `SSL_CERT_DIR=/etc/ssl/certs:/etc/platform-infra-ca` を描く。CA は ConfigMap `platform-infra-ca`（キー `ca.crt` だけ）を非 optional でマウントする。`global.db.tls.enabled` / `global.messaging.tls.enabled`（既定 true）は**切り戻し専用**で、検証を緩める値の受け口は持たない。本番像も既定で有効（ConfigMap は配備の側が用意する。無ければ Pod は立たない）。
5. **ブローカの名前の照合は共通ヘルパで強制する。** RabbitMQ.Client は amqps の URI を受けると `AcceptablePolicyErrors = RemoteCertificateNameMismatch` を入れる（実測）。`WolverineExtensions.UsePlatformRabbitMq` が TLS のときこれを空に戻し、7 サービスの `UseRabbitMq(new Uri(...))` を置き換える。
6. **CA は起動器が MSP と AST の名前空間へ写す。** `[5/7]` で `cert-manager/platform-infra-root-ca` の ca.crt だけを ConfigMap `platform-infra-ca` へ写す（毎回上書き。読めなければ止める）。AST の名前空間には置くだけで、AST の接続は変えない。
7. **compose も同じ形にする。** cert-manager が無いので、1 回きりのコンテナ `infra-tls-init`（ブローカのイメージの openssl）が同じ形の CA と葉証明書を作る（CA の鍵は署名の後に捨てる）。サーバの設定は経路B と同じ（`deploy/rabbitmq/20-tls.conf` は ConfigMap と同じ内容。試験が突き合わせる）。AST の 3 サービスは平文のまま（`x-rabbit-env-ast-plain`）。
8. **到達の制限を TLS の口へ寄せる。** `rabbitmq-ingress-clients` に 5671 を足す（5672 は AST のために残す）。経路B の egress（MSP）はブローカの 5671 だけを許す。
9. **稼働の効きを統合スタックの門で測る。** サーバの側の観測（`pg_stat_ssl`・`rabbitmqctl list_connections`）で MSP の利用者の接続がすべて TLS で 1 本以上あること、サーバ証明書を基盤の CA と素の名前で検証できること、別の名前では検証に失敗すること（対照）を要求する。

## 段の表

| 段 | 内容 | 前提 | 状態 |
| --- | --- | --- | --- |
| 4b-1 | 決定 1〜9 | — | 本 IADR |
| 4b-2 | AST の chart の接続を VerifyFull / amqps（＋ AST の Wolverine の呼び出しにも決定 5 と同じ名前の照合）へ。MSP の compose の AST の 3 サービス。Keycloak の DB・Wiki.js・バックアップの CronJob の接続 | 4b-1 の配備（CA の ConfigMap が AST の名前空間に在る） | 後続（AST は AST リポジトリ） |
| 4b-3 | 平文を閉じる（`hostssl` だけ・`hostnossl` 拒否、`listeners.tcp = none`、NetworkPolicy から 5672 を外す） | 4b-2 の切り替えを門で確かめた後 | 後続 |
| 4b-4 | valkey・qdrant・otel-collector・keycloak（HTTP）の TLS | — | 後続 |

## 実測（2026-10-23）

- 手元の docker（postgres 16-alpine・rabbitmq 3.13-management-alpine はいずれも本変更と同じ digest）で compose の `infra-tls-init`・postgres・rabbitmq を起こし、Npgsql 10.0.3 / RabbitMQ.Client 7.1.2 の .NET 10 クライアントで測った:
  - DB: VerifyFull ＋ 基盤の CA → `ssl=True TLSv1.3`。別の CA → 拒否。平文（併存）→ 接続できる。`psql sslmode=verify-full` も `t|TLSv1.3`。
  - ブローカ: amqps ＋ `SSL_CERT_DIR` → 接続。CA を足さない → 拒否。**素の URI 設定では別の名前（証明書に無い名前）でも接続できた**。受け入れてよい誤りを空にすると拒否（決定 5 の根拠）。
  - 受け口の版: TLS 1.1 は `no protocols available`、1.2・1.3 は検証 0。
  - `infra-tls-init` の 2 回目は何もしない（揃っている）。
- .NET 10 の `SSL_CERT_DIR` は `:` 区切りの複数のディレクトリを読み、ハッシュ名でないファイル（`ca.crt`）も読む。CA の無いディレクトリだけなら PartialChain で拒否、名前の不一致は NameMismatch で拒否。
- 統合スタック（k3d・`ISTIO=1`）での門の結果は PR の報告に記す。

## 結果

- 良い影響: 基盤のサービスと DB・ブローカの間が TLS になり、サーバ証明書が検証される（メッシュの外の区間）。CA は ClusterIssuer に閉じ、Vault PKI 等への差し替えは issuerRef で済む。
- 悪い影響・トレードオフ:
  - 併存の間（4b-3 まで）は平文の口が残る。AST と基盤の他のクライアントは平文で繋がる（門は数を出すだけ）。
  - 経路B の既定で cert-manager が入る（コンテナ 3 つ。limits は上流の既定のまま無い）。
  - 証明書の更新（30 日前）を postgres・rabbitmq は再起動まで読まない。期限（1 年）までに一度は作り直す（起動器の再実行で足りる）。
  - 再実行で postgres・rabbitmq が作り直される（ブローカは永続化していないので、未配送のメッセージは失われる）。
- フォローアップ: 段の表の 4b-2〜4b-4。本番像の CA の供給元（cert-manager の trust の配布か、配備の側の ConfigMap か）は本番の配備を決めるときに決める。
