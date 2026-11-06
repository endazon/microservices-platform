---
title: IADR-0674 取引ユニットをクラウドの単一ノードへ移すための MSP の最小構成を deploy/cloud に置く。基盤は変換を持たない kustomize、MSP は LLM ゲートウェイだけ、資格情報は初回に乱数で作り、秘匿管理（OpenBao）は最初の稼働では持ち込まない（#2401）
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR, ADR-0008, ADR-0124, IADR-0574, IADR-0591, IADR-0605, IADR-0672]
author: claude
created: 2026-11-05
updated: 2026-11-06
related_specs:
  - ../specs/20261105_2401_deploy-cloud-minimal.md
---

# IADR-0674: クラウドの最小構成（deploy/cloud。#2401）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> ［2026-11-05 追記 / #2401］番号は 0674 で確定した（develop の 0673 を取り込んだ時点の最大＋1。2026-11-05 の採番の取り決めどおり）。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: コーディネーターの指示（2026-11-05。PoC セッションの依頼）・claude（構成の形・秘匿の方式）

## 起点・関連

- 起点 issue: #2401。オーナーの決定（欧州の 8 GB・ニュースの RAG と Web の画面を持ち込まない・インフラ費の上限は月 5,000 円）。
- 計画: ADR-0008（k3s）・ADR-0124 決定 1（資格情報は対で書く・初期投入は無いときだけ作る）。計画の NFR（秘匿は Vault）との違いは planning#868 の 3 が計画側へ挙げている。
- 前提: IADR-0574 / IADR-0672（資格情報を宣言の開発用の値で作らない）・IADR-0591（基盤への接続は TLS・サーバ証明書を検証）・IADR-0605（自製イメージは不変のタグ）。

## コンテキスト

- `deploy/local/` と `scripts/k8s-local-up.sh` は手元の k3d / Rancher Desktop 専用で、開発用の値・画面・RAG・観測の一式を前提にする。
- 取引ユニットが MSP から要るのは、Postgres（取引ユニットの 7 つの DB。実測）・RabbitMQ（取引ユニットのサービスごとのユーザー）・Keycloak の `platform` レルム（取引ユニットの 5 つの機密クライアント）・LLM ゲートウェイだけである。
- ノードは 4 vCPU・8 GB の 1 台で、取引ユニットの全サービスと OpenD も同じノードに載る。

## 決定

### 決定 1 — 基盤は deploy/cloud/infra に、変換を持たない kustomize で置く

- resources だけを並べ、patches・images・generators を持たない（宣言＝描画。試験が宣言そのものへも同じ判定を当てられる）。
- ローカルの宣言を patch で流用しない。ローカルの宣言は開発用の字面（初期化の SQL のパスワード・Keycloak の H2 の資格情報・start-dev）を持ち、patch で打ち消す形は打ち消し漏れが描画に残る。
  - RabbitMQ はローカルの宣言をほぼそのまま写した（字面の資格情報を持たない）。到達の制限だけを取引ユニットのサービスに絞る。
  - 写した外部のイメージは deploy/local/infra と同じ digest に保つ（試験が突き合わせる。片方だけが古い版に取り残されない）。
  - RabbitMQ はローカルと違い、データ（`/var/lib/rabbitmq`）を PVC（2Gi）に置き、ノード名を固定し（`RABBITMQ_NODENAME`。既定はホスト名＝Pod 名で、Pod が替わるたびに別の置き場を作る）、Recreate で替える。
    ノードやブローカの再起動（更新・障害）の後へ、キュー・exchange・持続のメッセージを残す。キューは Wolverine の既定で durable（`RabbitMqQueue.IsDurable` の既定は true）。
    メッセージの持続は送り手の endpoint による —— Wolverine.RabbitMQ 6.24.5 の `RabbitMqSender` は `Persistent` を自分の `_isDurable` から設定する（アセンブリの IL で確かめた）。
  - Postgres の pg_hba は、TCP を `hostssl` だけで受ける（`host`・`hostnossl` を書かない。認証は scram-sha-256 だけ）。クラウドには平文で繋ぐ利用者がいない。ソケット（`local`）は初期化と `pg_isready` が使う。
    ソケットの行も scram-sha-256 にする（`peer`・`trust` にしない）。空のボリュームからの初回起動は、実物のイメージ（宣言と同じ digest）で通ることを確かめた:
    イメージの起動器は初期化の間 `POSTGRES_PASSWORD` を `PGPASSWORD` として psql に渡すので、`01-create-databases.sh` もパスワードで入れる。`pg_isready` は認証を要さない。
    `peer` は Pod の中の OS の利用者名と DB の役割名を結ぶが、管理者の役割名は乱数の接尾辞を持つので一致せず、役に立たない。`trust` はコンテナの中の任意のプロセスを管理者として通す。どちらも採らない。
    確かめは `scripts/deploy-cloud-postgres.test.js`（docker。CI）が固定する: 初回起動が終わる・8 つの DB ができる・ソケットはパスワード必須・TCP の平文は拒む・TLS は受ける。
  - サーバ証明書の鍵は、Postgres の条件（DB の利用者の所有なら 0600、root の所有なら 0640 以下）を満たす形で渡す。Secret のボリュームは root の所有で `defaultMode` が全ファイルに掛かり、
    Pod に `fsGroup` は置かない（置くとグループの読みが付き、root の所有でも 0640 を超え得る）。そこで **initContainer（`tls-files`。ローカルの IADR-0591 と同じ形）が Secret（0400）から
    Memory の emptyDir へ写し、所有者を 70:70（イメージの postgres）・鍵を 0600 にする**。Postgres はその emptyDir を読み取り専用で読む。command のラッパーで複写する案は、
    Postgres のコンテナに root の段を足すので採らない。docker の試験は、宣言の initContainer の command をそのまま root で走らせて同じ所有者と mode を作り（試験を走らせる利用者の uid に依らない）、
    所有者と mode を整える行を外すと起動しないこと（変異）も固定する。
- Postgres の初期化は、利用者名・パスワードを Secret から env で受け、psql の `\getenv` で読む（値を SQL・引数・ConfigMap に書かない）。管理者の利用者名も Secret から読む。データは PVC。
- Keycloak は本番モード（`start`）。DB は共有 Postgres（`sslmode=verify-full`）。hostname はクラスタの中の名前（`http://keycloak:8080`）だけ（画面を公開しないので、外向きのホストと証明書を持たない）。キャッシュは local（単一ノード）。
- 到達の制限（NetworkPolicy）を同じ kustomize に置く。platform-infra は入り・出とも既定で拒否し、要る組だけを許す。MSP の名前空間は出を既定で拒否し、LLM ゲートウェイだけに名前解決・Keycloak・collector・クラスタの外の 443 を許す。

### 決定 2 — MSP は chart の LLM ゲートウェイだけ（values-cloud.yaml）

- knowledge ユニットの全サービス・BFF・frontend・認可・MCP・通知・オブジェクトストレージ・Wiki.js・エッジ・メッシュ・HPA/PDB・同期の後の確かめを off にする。
- chart の NetworkPolicy は描かない（`networkPolicy.enabled=false`・`egress.enabled=false`）。chart の取引ユニットからの入り（`fromAst`）は `networkPolicy.enabled` の内側にあり、
  それを有効にすると chart の出の既定拒否も外せない（`networkpolicy-egress.yaml` が `enabled=true` と `egress.enabled=false` の組を描画で止める）。
- MSP の名前空間の入りは deploy/cloud/infra が既定で拒否し、LLM ゲートウェイへは取引ユニットの呼び出し元（報告書・取引判断。名前空間 かつ Pod）の 8080 だけを許す。
  送り元と行き先は chart の `fromAst.llmGateway` と同じ。入り・出とも 1 か所（`deploy/cloud/infra/networkpolicy.yaml`）に置き、宣言そのものを試験で固定する（helm を介さずに確かめられる）。
- chart の出の既定拒否（`networkpolicy-egress.yaml`）はクラスタの外への許可を描かない（宛先ごとの許可は IADR-0589 の段 2）。提供元の API へ出る LLM ゲートウェイには当てず、決定 1 の NetworkPolicy で絞る。
- 名前空間は deploy/cloud/infra が作る（基盤の別名と NetworkPolicy を chart より先に置く）。chart は `namespace.create=false`。

### 決定 2a — LLM ゲートウェイの受け口はアプリで TLS にする（メッシュを入れない）

- メッシュの無い単一ノードでは、取引ユニット → LLM ゲートウェイの通信（サービスのトークン・プロンプト・応答）が平文になる。2 案を比べた。
  - (a) アプリで TLS にする: Kestrel の受け口を https の 1 つにし、基盤の CA（bootstrap が作る）が発行した証明書を Secret `llmgateway-tls` で渡す。
  - (b) 単一ノードでもメッシュ（Istio）を入れる: 制御面と各 Pod のサイドカーの常駐（制御面だけで数百 MiB、サイドカーが Pod ごとに数十 MiB）を 8 GB のノードで取引ユニットから取る。
    PoC の経路B の構成（IADR-0317）と運用の手間もクラウドへ持ち込む。費用の上限（月 5,000 円）の中でノードを大きくする余地は無い。
- **(a) を採る。** CA の配り方（ConfigMap `platform-infra-ca` を名前空間へ写す。IADR-0591・IADR-0673）がそのまま使え、取引ユニットは CA と名前の両方で検証できる。
- chart に opt-in の口を足す（既定の描画は変わらない）: `services.<名>.tls.secretName` で `ASPNETCORE_URLS=https://+:<port>` と Kestrel の既定の証明書（PEM）を描き、probe を HTTPS にする。
  平文の受け口は残さない。gRPC の h2c の受け口は http でしか立たない（`GrpcListenerExtensions`）ので、`tls` と `grpcPort` の併記は描画で止める。
  取引ユニットは REST（`/complete`）だけを使うので、クラウドでは `grpcPort` を外す。

### 決定 2b — LLM ゲートウェイのルートのファイルシステムを読み取り専用にする

- `services.<名>.readOnlyRootFilesystem: true`（opt-in）でコンテナの `readOnlyRootFilesystem` を描き、書き込みの要る `/tmp`（ランタイムの診断の口など）は emptyDir（64Mi）で与える。
  ほかのマウント（証明書・CA・パイプラインの構成）は読み取り専用。LLM ゲートウェイはファイルへ書かない（実装を走査した）。

### 決定 3 — 資格情報は初回に乱数で作り、在れば触らない

> ［2026-11-06 追記 / #2471］**本決定は IADR-0689 が覆した**（AST の計画 ADR-0080 決定 1。クラスタの中で作る資格情報と TLS の鍵も OpenBao から供給するまでクラウドへ移さない）。資格情報は OpenBao の中で作り ESO が写す。証明書は OpenBao の PKI が発行する。`bootstrap-secrets.sh` は消した。本文は当時の記録として残す。

- `deploy/cloud/bootstrap-secrets.sh --live`（利用者が SSH のトンネル越しに実行する）が、DB の管理者・取引ユニットの DB の利用者・Keycloak の DB の利用者・Keycloak の管理者・ブローカの既定のユーザー・取引ユニットのサービスごとのユーザー・取引ユニットの 5 つの機密クライアントの secret を作る。
  - 利用者名にも乱数の接尾辞を付ける（宣言の字面の利用者名を使わない）。
  - 値は 0700 の一時ディレクトリのファイルからだけ kubectl へ渡し、表示しない。
  - 在る Secret は触らない（作り直しで稼働中の値と食い違わせない）。作り直しは IADR-0672 の作り直しのスクリプトか手順書。
- realm は `deploy/cloud/realm-for-cloud.js` が宣言から組む。取引ユニットの 5 つの機密クライアントと組み込みだけを残し、secret を乱数へ差し替え、人の利用者・試験の利用者・SMTP を外す。宣言の開発用の値・環境変数の置換が残れば止める。
- 証明書はその場で CA を作って発行し、**CA の鍵は保存しない**。作り直しは CA ごと（手順書）。
- ブローカの既定のユーザーは、無効化の印を最初から立てる（サービスごとのユーザーが揃えば起動のたびに消える。IADR-0577 と同じ仕組み）。

### 決定 4 — 秘匿管理（OpenBao / ESO）は最初の稼働では持ち込まない

> ［2026-11-06 追記 / #2471］**本決定は IADR-0689 が覆した**（AST の計画 ADR-0078 決定 5・ADR-0080）。クラウドに OpenBao と ESO を持ち込む。unseal の鍵とオーナー用の資格は age で暗号化してオーナーが手元に持つ。本文は当時の記録として残す。

- issue は「持ち込むなら本番の init にし、unseal 鍵を PVC に置かない」と求める。その形では、ノードの再起動のたびに人が unseal する必要がある。
  - ［2026-11-05 追記 / #2401］再起動の後は LUKS の解錠でどのみち人が要る（自動の再起動はしない。残余 4）ので、「人が要る」こと自体は決め手でない。
    unseal はその上に、もう 1 組の鍵（unseal 鍵）を人が持ち運ぶ手間と、解錠の順（LUKS → k3s → unseal → ESO の同期 → 取引ユニット）の 1 段を足す。
    守る対象の Secret は k3s の保存時の暗号化と LUKS で保存時に暗号化済みで、段を足す分の得が小さい。
  - 8 GB のノードで、OpenBao と ESO の常駐の分（数百 MiB）を取引ユニットから取る。
- 代わりに、Secret は k3s の保存時の暗号化（`--secrets-encryption`）とデータの領域の LUKS（パスフレーズはノードに置かない）で守る。秘密の値はリポジトリに置かない（暗号化したファイルも置かない。bootstrap が作る）。
- 計画の NFR（秘匿は Vault）と違う。違いは planning#868 の 3 が計画側へ挙げている（同件を起票しない）。計画の裁定で Vault が要るとなれば、別の段で持ち込む。

### 決定 5 — ノードの準備は手順書にする

- `deploy/cloud/README.md` に、ファイアウォール（SSH だけ・80/443/6443/50000 を開けない）・SSH の鍵認証・LUKS・chrony・更新の時間帯（JST 07:00〜21:00。自動の再起動はしない）・k3s のフラグ（traefik・servicelb を外す・`--secrets-encryption`・API サーバーはループバック）・k3s が暗号化した領域のマウントの前に起動しないこと（`k3s.service` の drop-in の `ConditionPathIsMountPoint` と `systemctl disable k3s`）・再起動の後の解錠の手順・SSH のトンネル・イメージの取り込みと digest の記録・配備の順序を書く。
- スクリプトにしないのは、LUKS のパスフレーズとファイアウォールの設定が対話と提供元の画面を要するため。項目は試験が固定する。

## 採らなかった案

- **deploy/local/infra を patch で流用する** — 決定 1 の理由（打ち消し漏れが描画に残る）。
- **SOPS（age）で暗号化した Secret をリポジトリに置く** — 鍵の管理が 1 つ増え、bootstrap が乱数で作れば置く物が無い。
- **OpenBao を dev モードで持ち込む** — 管理者のトークンが既知の値になり、決定 3 の趣旨に反する。

## 結果

- 描画（基盤・chart）は、持ち込まない資源・外への口・既定の資格情報・start-dev を持たない（`scripts/deploy-cloud.test.js`。CI）。
- 基盤の描画は、MSP の入りの既定拒否と LLM ゲートウェイへの許可（AC6）・Postgres の hostssl だけ（AC11）・RabbitMQ の永続（AC12）を持つ。chart は NetworkPolicy を描かない。
- chart の描画は、LLM ゲートウェイの受け口が TLS だけ（AC13）で、ルートのファイルシステムが読み取り専用（AC14）。描画は CI で確かめる（手元に helm が無い）。
- Keycloak（管理の面を持つ）はクラスタの中だけに置く。基盤の Service は ClusterIP と明示し、描画（基盤・chart）にエッジの資源（Ingress・Gateway・HTTPRoute ほか）を持たない（AC15）。
- 宣言そのものへ同じ判定を当てる試験・realm・bootstrap・手順書の試験は `scripts.repo.test.js` が持つ（素の環境で走る）。
- deploy/local は変えていない。

## 残余

1. 自製のイメージ（LLM ゲートウェイ）は不変のタグで固定し、digest は手順書で記録する。chart で digest を固定する口は持たない（chart の変更が要る）。
2. 取引ユニットの側の別名（`keycloak` 等）・Secret と CA の写し・接続の設定は AST#1525。
3. 観測の保存先と警報は MSP#2402（collector は受けて捨てる）。
4. **決定: 自動の再起動はしない**（`Unattended-Upgrade::Automatic-Reboot "false"`）。再起動の後は LUKS を人が開けて k3s を起こす（手順書 1-5）。
   理由: 無人で再起動すると k3s が上がらないまま止まる。k3s はマウントの前に起動しない（drop-in の `ConditionPathIsMountPoint=/var/lib/rancher` と `systemctl disable k3s` の二重）ので平文は出ないが、取引は止まる。
   再起動が要ることは `/var/run/reboot-required` で分かり、人が米国の場の時間外（JST 07:00〜21:00）に再起動して解錠する。止まったままになったことは外からの生存の確かめ（MSP#2402 の heartbeat）で気付ける。
5. 単一ノードには冗長が無い（planning#868 の 4）。
6. ブローカの再起動の後に残るのは、送り手が持続で出したメッセージだけ。取引ユニットで持続で出すのは注文の実行のサービスだけ（送り先の exchange を Durable にしている）で、ほかの送り手（承認を出す側など）は非持続で出す。送り手の側の設定は取引ユニットの所有（MSP の宣言では変えられない）。
7. RabbitMQ の平文の 5672 は、ローカルの段 1 と同じくブローカでは開けたまま。届く範囲は NetworkPolicy が 5671（TLS）だけに絞る。
8. 取引ユニットの側は、LLM ゲートウェイの接続先を https にし、CA（ConfigMap `platform-infra-ca`）で検証する必要がある（AST#1525）。揃うまで取引ユニットからの呼び出しは繋がらない（平文へは倒さない）。
9. LLM ゲートウェイ → Keycloak（トークンの検証の鍵の取得）は、クラスタの中の http のまま。届く範囲は NetworkPolicy が絞る。
