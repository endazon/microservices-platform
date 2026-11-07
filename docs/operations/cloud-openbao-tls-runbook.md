---
title: 運用 Runbook — クラウドの秘匿管理（OpenBao）の受け口の TLS（新しく作る・稼働中の構成を移す・取引ユニットの適用の順序・証明書の作り直し）
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-16, NFR-05, NFR-18]
iadrs: [IADR-0696, IADR-0689, IADR-0693, IADR-0682, IADR-0703]
specs: [20261107_cloud-openbao-tls]
-->

# 運用 Runbook: クラウドの秘匿管理（OpenBao）の受け口の TLS

> 対象はクラウドの最小構成（単一ノードの k3s・`deploy/cloud/`）の OpenBao と、それに話す ESO のストア（基盤と取引ユニット）・オーナーの手順（`deploy/cloud/vault/*.sh`）。
> 初期化・unseal・root の作り直しは[秘匿管理の手順書](cloud-openbao-init-unseal-runbook.md)にある。
>
> **状態を `draft` に留めている理由**: クラウドのノードで移行をまだ流していない（移行の門の稼働の確かめで記録する）。

## 形（何がどう変わったか）

| 項目 | 平文の構成（旧） | TLS の構成（最終の段） |
| --- | --- | --- |
| OpenBao の受け口 | 平文の 8200 | **TLS の 8443 だけ**（TLS 1.3 以上） |
| 受け口の証明書 | なし | `deploy/cloud/vault/tls-cert.sh` がオーナーの手元で作り、Secret `openbao/openbao-tls` に置く（CA の鍵は署名の後に捨てる） |
| ESO のストア | `http://openbao.openbao.svc:8200` | `https://openbao.openbao.svc:8443` と、ConfigMap `openbao-ca`（ストアと同じ名前空間）の CA で検証 |
| オーナーの手順（Pod の中の bao） | 平文のループバック | `https://127.0.0.1:8443` と受け口の CA（`lib.sh` の `ob_exec` が明示する） |
| 届く相手 | ESO の controller だけ（8200） | 同じ（8443） |

- 受け口の証明書は OpenBao の PKI では作らない。封印のあいだは発行できず、受け口が無いと ESO もオーナーの手順も OpenBao へ届かないためである。
- 併存の段（平文の 8200 と TLS の 8443 の両方）は `deploy/cloud-transition/openbao-tls-coexist`（稼働中の構成を移す間だけ当てる）。
- スクリプトはどれも `--live` が無ければ何もしない。値は表示しない。鍵は引数・環境変数・ディスクに置かない（手元のプロセスのメモリと標準入力だけを通る）。

## 1. 新しく作る構成

`deploy/cloud/README.md` §3 の順のままでよい。要点は、**OpenBao を起こす前に受け口の証明書を置く**ことである。

```sh
kubectl apply -f deploy/cloud/infra/namespaces.yaml
kubectl apply -k deploy/cloud/vault                    # OpenBao は Secret openbao-tls を待って起きない（ContainerCreating）
bash deploy/cloud/vault/tls-cert.sh --live issue        # 受け口の証明書と CA の ConfigMap を置く → OpenBao が起きる（封印された状態）
bash deploy/cloud/vault/init.sh --live …               # 以降は秘匿管理の手順書のまま（init → unseal → configure → seed）
```

- 確かめ: `kubectl -n openbao get pods` の Pod が Running（Ready は unseal の後）。`tls-cert.sh` の出力に「置いた: ConfigMap platform-infra/openbao-ca」「… microservices-platform/openbao-ca」。
- 取引ユニットの名前空間がまだ無ければ、`tls-cert.sh` は「まだ無い: 名前空間 ai-stock-trading」と出す。§3 のとおり、取引ユニットを入れた後に `publish-ca` を流す。

## 2. 稼働中の構成を移す（平文 → 併存 → 平文を閉じる）

🔴 **順を守る。** ストアの宛先（http か https か）と受け口の形が合わない時間を作らない。各段の後に「確かめ」を見てから次へ進む。
OpenBao の作り直しは段 1 と段 4 の 2 回で、どちらの後も unseal が要る（封印のあいだ ESO は同期できないが、写した Secret は残り、消費側は動く）。

### 段 0: 受け口の証明書を置く

```sh
bash deploy/cloud/vault/tls-cert.sh --live issue
```

- OpenBao は作り直さない（Secret と ConfigMap を置くだけ）。ESO は平文のまま同期を続ける。
- 確かめ: 出力に 3 つの名前空間の「置いた: ConfigMap …/openbao-ca」（取引ユニットの名前空間は稼働中の構成なら在る）。

### 段 1: OpenBao を併存にする（8200 と 8443 の両方）

```sh
kubectl apply -k deploy/cloud-transition/openbao-tls-coexist
kubectl -n openbao rollout status deploy/openbao --timeout=300s   # Ready にならないのが正しい（封印されている）。Running を確かめる
bash deploy/cloud/vault/unseal.sh --live --init-file <init>.age --identity <age の識別子>   # このリポジトリの版の手順（TLS で話す）
```

- 確かめ: unseal の出力に「書き込みを受ける状態（active）になった」。`kubectl get secretstore -A` の基盤・取引ユニットのストアが Ready（まだ平文の 8200 で届いている）。

### 段 2: 基盤のストアを https にする

```sh
kubectl apply -k deploy/cloud/vault/eso
kubectl -n platform-infra get secretstore openbao        # Ready
kubectl -n microservices-platform get secretstore openbao
kubectl -n platform-infra annotate externalsecret postgres force-sync="$(date +%s)" --overwrite   # 1 つを同期させて確かめる
```

- 確かめ: ストアが Ready で、`kubectl -n platform-infra get externalsecret postgres` の状態が SecretSynced。VaultDynamicSecret の証明書の作り直しも https で行われる。

### 段 3: 取引ユニットのストアを https にする

取引ユニットの「ストアを https と CA にする」変更（取引ユニットの別の PR）を入れた chart を、取引ユニットの手順書どおりに当て直す。

- 🔴 **取引ユニットの変更は、MSP の段 0〜段 1 の後に当てる。** 先に当てると、取引ユニットのストアが平文の受け口へ https で話して届かない。
- 確かめ: `kubectl -n ai-stock-trading get secretstore openbao` が Ready。取引ユニットの ExternalSecret の 1 つを force-sync して SecretSynced。

### 段 4: 平文を閉じる（最終の段）

全てのストア（`kubectl get secretstore -A`）が https・Ready なのを確かめてから:

```sh
kubectl apply -k deploy/cloud/vault
kubectl -n openbao delete configmap openbao-config-coexist   # 併存の段だけの設定（もう読まれない）
kubectl -n openbao rollout status deploy/openbao --timeout=300s
bash deploy/cloud/vault/unseal.sh --live --init-file <init>.age --identity <age の識別子>
```

- 確かめ: `kubectl -n openbao get svc openbao -o jsonpath='{.spec.ports[*].port}'` が `8443` だけ。ストアが Ready のまま。
  平文の口が残っていないことは、描画の試験（`node scripts/deploy-cloud.test.js`）が最終の段で固定している。
- 確かめ（併存の段の残り）: `kubectl -n openbao get configmap openbao-config-coexist --ignore-not-found -o name` が何も出さず、
  `kubectl -n openbao get deploy openbao -o jsonpath='{.spec.template.spec.containers[0].command}'` に `coexist` が無い。
  出たら、消し忘れか最終の段を当て損ねている（ConfigMap を消し、`kubectl apply -k deploy/cloud/vault` を当て直す）。
- 段 4 の確かめの結果は、移行の門の稼働の確かめで記録する（本書を `draft` に留めている理由）。

### 切り戻し

- 段 2・段 3 の後にストアが Ready にならない: 併存の段のまま、ストアを前の版（http の 8200）へ戻して当てる（受け口は両方を受けている）。理由（CA の ConfigMap の欠落など）を直してからやり直す。
- 段 4 の後に届かない: 併存の重ねを当て直す（段 1 と同じ。作り直し → unseal）。

## 3. 取引ユニットの適用の順序と CA の受け取り方

- **MSP（本書の段 0〜段 2）の後に、取引ユニットのストアを https にする**（段 3）。最終の段（段 4）は取引ユニットのストアが https になった後。
- 取引ユニットのストアは、同じ名前空間の ConfigMap `openbao-ca`（キー `ca.crt`）で受け口を検証する。この ConfigMap は MSP の `tls-cert.sh` が置く。
  取引ユニットを新しく入れるとき（名前空間が後から作られるとき）は、入れた後に置く:

```sh
bash deploy/cloud/vault/tls-cert.sh --live publish-ca --namespace ai-stock-trading
```

  ストアは ConfigMap が置かれるまで Ready にならない（ESO が繰り返し試す）。置いた後に Ready になる。

## 4. 受け口の証明書の作り直し

- 受け口の証明書の期限は 1 年（CA は 2 年）。**半年ごと**と、鍵が漏れた疑いのときに作り直す。

```sh
bash deploy/cloud/vault/tls-cert.sh --live renew
```

- `renew` は 1 回で次を行う（どの時点でも ESO とオーナーの手順は受け口へ届く。封印しない。unseal の鍵も root も要らない）:
  1. 新しい CA と証明書を作り、**古い CA と新しい CA の束**を ConfigMap `openbao-ca` と Secret の `ca.crt` に置く。
  2. 新しい証明書が Pod へ届くのを待ち（kubelet が Secret を届けるまで。上限 180 秒）、受け口に読み直させる（Pod の中で SIGHUP）。
  3. Pod の中から**新しい CA だけ**で届くことを確かめる。
  4. 古い CA を外す。
- 途中で落ちたら: 束（古い CA と新しい CA）のままで止まる。ESO は届く。理由を直して `renew` を流し直せばよい（流し直しは CA ごと作り直す）。
- 期限は公開の部分だけを読む（鍵 `tls.key` は読まない）:
  `kubectl -n openbao get secret openbao-tls -o jsonpath='{.data.tls\.crt}' | base64 -d | openssl x509 -noout -enddate`
- 半年ごとの作り直しを、基盤の証明書の作り直し（`deploy/cloud/README.md` §6）と同じ日に行う。
- ［2026-11-07 追記］期限の警報 `OpenBaoListenerCertExpiringSoon` を置いた（残り 150 日）。受け口は ESO だけが届くので測りに行かず、`renew` が同じ回に作る
  CA（2 年）と受け口の証明書（1 年）の関係から、ConfigMap `openbao-ca` の最新の CA の notAfter − 365 日で導く。`renew` の日数を変えるときは警報の式も変える（試験が突き合わせる）。
