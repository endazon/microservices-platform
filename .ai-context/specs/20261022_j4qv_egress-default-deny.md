---
title: 作業仕様書 — 外向き通信（egress）を既定で拒否し、要る宛先だけを許す（段 1。GHSA-j4qv-2w99-xg5p）
type: spec
status: done
related_ids: [NFR-18, ADR-0005, ADR-0008, IADR-0589, IADR-0026, IADR-0461, IADR-0513, IADR-0520, IADR-0522, IADR-0573, IADR-0583]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md（§非LLM 外部送信の統制 §基本原則: egress は既定拒否・許可先を allowlist で管理する）
  - planning:projects/microservices-platform/07_adr/ADR-0005（サービスメッシュ）
  - planning:projects/microservices-platform/07_adr/ADR-0008（k3s 実行基盤）
issue: "GHSA-j4qv-2w99-xg5p"
---

# 作業仕様書 — 外向き通信（egress）の既定拒否（段 1。GHSA-j4qv-2w99-xg5p）

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。判断の記録は **IADR-0589**（新規）に置く。
> 多層防御の強化（最小権限）。参照は GHSA の ID だけとする。

## 起点となる計画書（トレーサビリティ）

- 技術検討: **08_data-egress-policy**（fixed）§非LLM 外部送信の統制 §基本原則「egress（下り）ネットワークは既定拒否（default-deny）とし、許可先を allowlist で管理する」、§リスク・未決事項「default-deny ネットワークポリシーの実装確認（実装リポジトリへ引き継ぎ）」。本変更はその実装の段 1 であり、許す範囲を広げない。
- 計画 ADR: **ADR-0005**（メッシュ。サイドカー → istiod の口を許す）・**ADR-0008**（k3s。NetworkPolicy を強制するコントローラは k3s が内蔵する）。
- 非機能要求: **NFR-18**（同じ GHSA の他の変更と同じ起点）。
- 実装 ADR: **IADR-0589**（新設）。前提 **IADR-0026**（NetworkPolicy は多層防御として維持）・**IADR-0461** 決定 10（SeaweedFS の ingress）・**IADR-0513**（AST からの ingress）・**IADR-0520**（製品の既定の外部通信の無効化）・**IADR-0522** 決定 4（経路 B の NetworkPolicy の強制は未実測と記録）・**IADR-0573**（ブローカの ingress）・**IADR-0583**（同じ GHSA の Pod 側）。

## 要件

1. チャートが描く Pod の egress を**既定で拒否**し、要る宛先だけを許す。DNS は **kube-dns の Pod の 53 番だけ**へ許す。
2. 本番像（`networkPolicy.enabled: true`）は名前空間の**全 Pod** に当てる。許すのは DNS・同じ名前空間・istiod（`mesh.enabled`）と、従前からのサービス固有の 1 本（Vault・API サーバ・合成監視 → Keycloak）だけ。
3. 経路 B（`deploy/local/values-local.yaml`。ingress の NetworkPolicy は従前どおり描かない）にも egress の既定拒否を当てる。**段 1 はクラスタの外へ出ない Pod だけ**。基盤（platform-infra）への宛先は 1 件ずつ宣言する。
4. 宣言の欠け・食い違いは描画で止める（fail-closed）。宛先の送り元はサービスの宣言（DB・ブローカ・OTLP・Qdrant）とそろえ、試験で突き合わせる。
5. 稼働のクラスタで「効いている」ことを統合スタックの門で測る（陽性・陰性・対照）。

## 母集合（規則 9・10）

走査: `helm template`（既定・values-local・`mesh.enabled=true`）が描く Deployment / Job の全件と各 Pod の env、`grep -rn "egress\|NetworkPolicy\|outboundTrafficPolicy\|ServiceEntry\|kind: Sidecar" deploy`、`src/*/backend/Services/*/appsettings.json` の接続先、`grep -rln "AddHttpClient\|GrpcChannel\|SmtpClient\|MailKit" src --include=*.cs`（試験を除く）、`deploy/local/aliases/*.yaml`（ExternalName の行き先）、`deploy/local/infra/*.yaml`（基盤の Pod のラベルとポート）。

### NetworkPolicy・Istio の資源（変更前の宣言）

| 置き場 | 名前 | 種類 | 本変更 |
| --- | --- | --- | --- |
| chart `networkpolicy.yaml` | default-deny-ingress / allow-edge-ingress-* / allow-ast-*-ingress | Ingress | 変えない |
| 同上 | allow-intra-namespace | Ingress ＋ Egress（同じ名前空間・DNS） | **Egress を外し** `networkpolicy-egress.yaml` へ移す（DNS は kube-dns の Pod に絞る） |
| 同上 | allow-<svc>-egress-to-vault / allow-bff-egress-to-kube-apiserver | Egress（1 本） | 変えない（描く条件も `networkPolicy.enabled` のまま） |
| chart `seaweedfs.yaml` | allow-seaweedfs-s3-only | Ingress ＋ Egress | DNS の宛先だけ kube-dns に絞る（C# の試験が ingress の字面を読むので節の形は保つ） |
| chart `synthetic-monitor.yaml` | allow-synthetic-monitor-egress-to-keycloak | Egress（1 本） | 変えない |
| chart `istio-mtls.yaml` / `edge.yaml` | PeerAuthentication・AuthorizationPolicy | mesh | 変えない。Sidecar・ServiceEntry・`outboundTrafficPolicy` は置いていない（本変更でも置かない） |
| `deploy/local/infra` | valkey-ingress-bff-only（egress は `[]`）・rabbitmq-ingress-clients | Ingress | 変えない |
| `deploy/local/observability`・`deploy/mail-relay` | loki / tempo / mail-relay の ingress | Ingress | 変えない |

- クラスタの外への許可を宣言する既存の仕組みは `apiServerEgress.cidrs`（ipBlock・宣言したときだけ描く）だけである。Istio の ServiceEntry は使っていない。**段 1 はクラスタの外への許可を 1 つも描かない**ので、新しい仕組みは足さない（段 2 で ipBlock の宣言として足す）。

### Pod ごとに要る宛先と段

| Pod（app） | 同じ名前空間 | platform-infra | その他 | 段 |
| --- | --- | --- | --- | --- |
| aianalysis-service | retrieval・llmgateway・authorization | keycloak 8080・otel 4317 | — | 1 |
| authorization-service | — | postgres 5432・keycloak 8080（管理 API を含む）・otel | — | 1 |
| conversion-service | llmgateway・seaweedfs | postgres・rabbitmq 5672・keycloak・otel | — | 1 |
| dashboard-service | — | postgres・keycloak・otel | — | 1 |
| document-service | notification・authorization・seaweedfs | postgres・rabbitmq・keycloak・otel | — | 1 |
| feedback-service | — | postgres・keycloak・otel | — | 1 |
| graph-service | llmgateway・authorization・dashboard・document・seaweedfs | postgres・rabbitmq・keycloak・otel | — | 1 |
| ingestion-service | llmgateway・document・seaweedfs | rabbitmq・keycloak・otel・qdrant 6333/6334 | — | 1 |
| mcp-service | authorization・document・retrieval・graph | postgres・keycloak（管理 API を含む）・otel | — | 1 |
| notification-service | — | postgres・keycloak・otel | — | 1 |
| retrieval-service | llmgateway・graph・authorization | rabbitmq・keycloak・otel・qdrant 6333（health）/6334 | — | 1 |
| wiki-service | authorization・wiki-js・seaweedfs | postgres・rabbitmq・keycloak・otel | — | 1 |
| frontend-service（Caddy） | bff-service | — | — | 1 |
| seaweedfs | 自身 | — | — | 1 |
| bff-service | 各サービス | valkey・vault・keycloak・otel | AST の名前空間（configuration / risk-management / market-monitor / opend）・API サーバ（ESO=1） | 2 |
| llmgateway-service | embedding | keycloak・otel | LLM・埋め込みの提供元（クラスタの外 443） | 2 |
| datasource-service | authorization・seaweedfs | postgres・rabbitmq・keycloak・otel・vault | SaaS コネクタの取得元（クラスタの外） | 2 |
| wiki-js | — | postgres | エッジの issuer（`keycloak.localhost`） | 2 |
| config-drift-postsync（Job） | 各サービス | — | — | 2 |
| embedding（既定で描かない） | — | — | モデルの取得元（未配置なら外） | 2 |
| synthetic-monitor（既定で描かない） | bff-service | keycloak | — | 2 |

- 確かめた事実: 段 1 のサービスに SMTP・外部 HTTP の送り先は無い（`SmtpClient` / `MailKit` は 0 件。メールは認可サーバと Alertmanager が中継へ送る）。Qdrant の接続を持つのは Retrieval と Ingestion の appsettings だけ。`Otlp__Endpoint` は全 .NET サービスに描かれる。
- 段 1 の宛先は values-local の `networkPolicy.egress.destinations` に 5 件（postgres・rabbitmq・keycloak・otel-collector・qdrant）。送り元は上の表と一致する（試験が描画した env と突き合わせる）。
- 範囲外（報告のみ）: AST の名前空間（AST のチャートが持つ）。platform-infra の基盤の egress（valkey 以外は制限なし）は本変更の対象外とし、段 3 に置く。

### NetworkPolicy を強制するか（CNI）

- 経路 B の起動器が作るクラスタ（k3d・Rancher Desktop）はどちらも k3s であり、k3s は NetworkPolicy のコントローラ（kube-router 由来）を同梱し既定で有効にする。
- **手元で実測した**（2026-10-22）: k3s **v1.35.4+k3s1**（起動器の既定の K3S_IMAGE と同じ digest）を docker で起こし、`networkPolicy.egress` の描画（values-local）をそのまま当てた。段 1 の Pod からは宣言した宛先（ClusterIP の Service 名で引いた postgres・keycloak・otel・qdrant・rabbitmq）だけに届き、宣言の無い宛先（valkey・別のサービスのポート・API サーバ・クラスタの外）は落ちた。workloads に無い Pod（対照）は全てに届いた。ClusterIP は宛先の Pod の IP に変換された後で判定される（Service 名で引いても Pod の selector が効く）。
- **起動の直後の窓**: 新しい Pod の IP へ規則が入るのは非同期で、起動の直後（実測で数秒）は宛先に届いた。統合スタックの門は Pod の中で 20 秒待ってから測る。サービスの Pod にも同じ窓があることは IADR-0589 の残余に書く。
- NetworkPolicy を強制しないクラスタ（`--disable-network-policy` の k3s・強制しない CNI）では宣言が残るだけになる。統合スタックの門が赤で知らせる。

## 段の分け方

| 段 | 範囲 | 状態 |
| --- | --- | --- |
| **1（本変更）** | チャートに egress の既定拒否の組を置く（本番像は全 Pod・DNS を kube-dns へ絞る・istiod を許す）。経路 B は外へ出ない 14 の Pod（＋確かめ用の `egress-probe`）に当て、基盤の 5 宛先を宣言する。静的な試験と統合スタックの門 | 本 PR |
| 2 | 経路 B の残り（bff・llmgateway・datasource・wiki-js・config-drift-postsync・embedding・synthetic-monitor）。クラスタの外への許可（LLM・埋め込みの提供元・SaaS の取得元）を ipBlock の宣言として足す（既定は空＝出られない）。API サーバの宛先（`apiServerEgress.cidrs`）を起動器が稼働のクラスタから与える。AST の名前空間への宛先 | 後続 |
| 3 | 本番像の宛先（基盤が別の名前空間に居る配備の `destinations`・クラスタの外の許可の具体値）を環境ごとに宣言する。platform-infra の基盤の egress | 後続（計画 08_data-egress-policy の allowlist の具体値と合わせる） |

## 実装

- `templates/_networkpolicy.tpl`（新規）: DNS の規則（kube-dns の Pod の 53 番）と app ラベルの podSelector の部品。
- `templates/networkpolicy-egress.yaml`（新規）: `default-deny-egress`・`allow-egress-dns`・`allow-egress-intra-namespace`・`allow-egress-istiod`（`mesh.enabled`）・`allow-egress-to-<name>`（destinations）。描く条件は `networkPolicy.enabled || networkPolicy.egress.enabled`。描画で止める: `enabled` のまま `egress.enabled=false`・`enabled` のまま workloads を絞る・`enabled=false` で workloads が空（全 Pod へ黙って描かない。レビューの指摘で追加）・空の workloads の要素・destinations の欠けた値 / 重複 / 不正な name・workloads に無い from・空の DNS / istiod の値。
- `templates/networkpolicy.yaml`: allow-intra-namespace を Ingress だけにする。コメントの追随。
- `templates/seaweedfs.yaml`: egress の DNS を部品に替える。
- `values.yaml`: `networkPolicy.egress`（enabled・workloads・dns・istiod・destinations）。
- `deploy/local/values-local.yaml`: `networkPolicy.egress`（enabled・段 1 の workloads・5 宛先）。
- 試験 `scripts/helm-egress-network-policy.test.js`（新規）と CI の段（static-checks-units。起動条件・必須チェックは不変）。
- `integration-stack.yml`: 門「外向き通信の既定拒否（NetworkPolicy の強制）」を足す（ログイン経路の門より前。T-25 の偶然の赤の印の条件にも足す）。起動条件は変えない（PR の push では走らない）。
- 文書: `deploy/local/README.md`・`docs/security/security.md`・`docs/operations/east-west-grpc-h2c-roundtrip-measurement-runbook.md` の NetworkPolicy の記述。

## 受け入れ基準

- [x] 本番像: 全 Pod に既定拒否。DNS は kube-dns の 53 番だけ（全名前空間の 53 番の規則が残らない）。同じ名前空間・istiod（mesh）は通る。別の名前空間・クラスタの外は落ちる（評価器）。
- [x] values-local: 描画される全 Pod が段 1 / 段 2 のどちらかに分類され、段 1 の Pod は宣言した宛先だけに届く。段 2 の Pod は変わらない（評価器）。
- [x] 宛先の送り元が env の宣言と一致し、宛先が deploy/local/infra の実体と一致する。
- [x] 描画で止まる条件が効く。変異（DNS の podSelector・宛先の AND・ports・from の検査）で試験が赤になる。
- [x] k3s v1.35.4 の実クラスタで、門の手順が緑になり、egress の NetworkPolicy を消すと赤になる（手元で実測）。
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・helm lint・check-deploy-manifests・`k8s-local-up.test.js`・文書系の検査が緑。

## 配備（PoC）

- 配備は閉場後（基盤との通信の経路に規則が入る。取引の時間帯に MSP のサービスが再起動・接続し直すことを避ける）。
- 手順は PR 本文に書く（追加の値・資格情報は無い。起動器の再実行か同じ values での `helm upgrade`。`egress-probe` で確かめる）。
