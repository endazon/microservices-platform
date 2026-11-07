---
title: IADR-0696 クラウドの秘匿管理（OpenBao）の受け口を TLS の 8443 だけにする。受け口の証明書は OpenBao の外でオーナーが流すスクリプトが作り（CA の鍵は署名の後に捨てる）、ESO とオーナーの手順は CA で検証する。稼働中の構成は併存の段を経て平文を閉じる（IADR-0689 決定 3 を改める）
type: impl-adr
status: Accepted
related_ids: [NFR-16, NFR-05, NFR-18, IADR-0689, IADR-0693, IADR-0682, IADR-0525, IADR-0703]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_cloud-openbao-tls.md
---

# IADR-0696: クラウドの OpenBao の受け口を TLS にする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 利用者の裁定（2026-11-07。OpenBao も TLS にする）・claude（証明書の出どころ・移る段の形）

## 起点・関連

- 要求 **NFR-16**（通信の暗号化）・**NFR-05**（秘匿は OpenBao）・**NFR-18**。
- 改める: **IADR-0689 決定 3**（受け口は平文。届く相手を NetworkPolicy で ESO だけに絞る）と**残余 3**。改めた旨は IADR-0689 に日付つきで追記した。
- 前提: IADR-0689 決定 1・2（init・unseal・root の失効の順序）、IADR-0682（秘密を引数・環境・履歴・ディスクに置かない）、IADR-0693（併存の段の型）。
- 同じ裁定の「基盤の Pod の権限」は別の作業が持つ（本 IADR の範囲の外）。

## コンテキストと課題

- ESO と OpenBao の間は、写す値の全部と ESO のトークンが流れる。オーナーの手順（`kubectl exec` の先の Pod の中の bao）も同じ受け口を使う。
  受け口が平文のままだと、到達を絞っても通信路の上の値は守られない（利用者の裁定 2026-11-07 は、この通信を暗号化することを求めた）。
- 実測（作業仕様書 §実測。手元の OpenBao 2.7.1）: listener は TLS だけで受けられ、平文の要求を拒む。SIGHUP で受け口の証明書を読み直し、封印は変わらない。
  `-config` を 2 つ与えると listener が足し合わされる。OpenBao のイメージは openssl を持たない。

## 決定

### 決定 1 — 受け口の証明書は、OpenBao の外でオーナーが流すスクリプトが作る。CA の鍵は署名の後に捨てる

`deploy/cloud/vault/tls-cert.sh`（`--live` が無ければ何もしない）。

- `issue`（初回）: 手元のプロセスのメモリで CA（ECDSA P-256・2 年）と受け口の証明書（同・1 年。SAN は Service の 4 つの形とループバック `127.0.0.1` だけ）を作り、
  Secret `openbao/openbao-tls`（`tls.crt`・`tls.key`・`ca.crt`）と ConfigMap `openbao-ca`（`platform-infra`・`microservices-platform`・在れば `ai-stock-trading`）に置く。
  鍵は openssl へプロセス置換（パイプ）で、kubectl へ標準入力で渡し、引数・環境変数・ディスクに置かない（IADR-0682 の形）。Secret が在れば止まる。
- **CA の鍵は受け口の証明書に署名した直後に捨てる**（どこにも残さない。オーナーの持つファイルは init と operator の 2 つのまま）。
- `renew`（作り直し）: CA ごと作り直す。① 古い CA と新しい CA の束を ConfigMap と Secret の `ca.crt` に置く → ② 新しい証明書が Pod へ届いたら SIGHUP で読み直させる（封印しない）→
  ③ Pod の中から**新しい CA だけ**で届くことを確かめる → ④ 古い CA を外す。ESO は束で検証するので、どの時点でも届く。途中で落ちても束のままで、流し直せば終わる。
- `publish-ca`: Secret の `ca.crt`（公開してよい）を ConfigMap へ写し直す（取引ユニットを後から入れたとき）。

採った理由（選ぶ条件に照らす）:

| 条件 | 本決定 |
| --- | --- |
| 初回の起動・unseal・root の失効（IADR-0689）の順序が壊れない | 証明書は OpenBao を起こす**前**に置く（Pod は Secret が無ければ起きない）。init・unseal・configure の順はそのまま、前に 1 段が足されるだけ。どの段も OpenBao の状態（封印・root）に依らない |
| 作り直しの手順がある | `renew` の 1 回で併記 → 読み直し → 確かめ → 外すを行う。封印せず、unseal の鍵も root も要らない |
| 値はスクリプトが作り、所有者が流す | `tls-cert.sh` が作り、オーナーが手元から流す。値は表示しない |

採らなかった:

- **OpenBao の PKI で自分の証明書を作る（鶏と卵）**: 封印のあいだは発行できない。初回は PKI がまだ無く、作り直しでは ESO（受け口を検証して届く側）が受け口の証明書を運ぶことになり、
  期限切れや失敗で受け口が壊れると、それを直す経路（ESO・オーナーの手順）も受け口を通るので届かない。初回のための別の証明書がどのみち要り、出どころが 2 つになる。
- **外で作る CA と鍵を、オーナーが流すスクリプトで作って Secret に置き、CA の鍵を持ち続ける**: 作り直しで CA を替えずに済むが、CA の鍵を age で暗号化した 3 つ目のファイルとして
  オーナーが 2 か所に保管することになる。CA の鍵を捨てても、`renew` が CA の併記を同じ 1 回の中で行うので、作り直しの手間は増えない。
- **OpenBao の PKI の CA を後から使う（初回は自己署名、configure の後に `pki_cloud` から自分の証明書を発行して差し替える）**: 上の鶏と卵の作り直しの問題が残る。さらに ESO が
  `pki_cloud` の CA（基盤の Postgres・RabbitMQ・Keycloak・LLM ゲートウェイの証明書と同じ CA）を受け口の検証にも使うことになり、受け口の信頼の根が基盤の証明書の発行元と同じになる。
- dev の自己署名（`-dev-tls`）・検証を外す指定: 本番の init を使う（IADR-0689）。検証を外せば暗号化の意味が無い。

### 決定 2 — 受け口は TLS の 8443 だけ。平文の listener を持たない

- `listener "tcp"` は `0.0.0.0:8443`・`tls_cert_file`／`tls_key_file`（Secret `openbao-tls` のマウント `/openbao/tls`。fsGroup で 0440・optional にしない）・`tls_min_version = "tls13"`。
  `tls_disable` を書かない。`api_addr` は https。Service・NetworkPolicy の入りは 8443 だけ。readiness は HTTPS。
- ポートを 8200 から替えるのは、移る間に平文（8200）と TLS を同時に受けるため（同じポートでは両方を受けられない）。最終の段の 8443 は併存の段の TLS の口と同じで、
  ストアの切り替えは 1 回で済む（Keycloak の 8080 → 8443 と同じ形。IADR-0693）。
- 入りの送り元を ESO の controller だけに絞る NetworkPolicy（IADR-0689 決定 3）は残す（TLS と重ねる）。

### 決定 3 — ESO とオーナーの手順は、受け口の CA で検証する

- ESO の SecretStore 2 つ・VaultDynamicSecret 4 つは `https://openbao.openbao.svc:8443` と `caProvider: { type: ConfigMap, name: openbao-ca, key: ca.crt }`。名前空間のストアなので、
  ConfigMap はストアと同じ名前空間に置く（決定 1 が置く）。検証を外す指定は使わない。
- オーナーの手順（`lib.sh` の `ob_exec` を使う全部）は、Pod の中でも `BAO_ADDR=https://127.0.0.1:8443`・`BAO_CACERT=/openbao/tls/ca.crt` を明示し、検証を外す env（`*_SKIP_VERIFY`）を消す。
  Pod の env に頼らない（移る間の Pod が古い env を持っていても TLS で話す）。宛先は https の形だけを受ける。
- 取引ユニットの名前空間のストア（取引ユニットの chart）も同じ宛先と CA（同じ名前空間の ConfigMap `openbao-ca`）に移す（取引ユニットの別の PR。MSP の後に当てる）。

### 決定 4 — 稼働中の構成は、併存 → 切り替え → 平文を閉じる、の順で移す

- 併存の重ね `deploy/cloud-transition/openbao-tls-coexist`（`deploy/cloud/vault` に重ね、平文の 8200 の listener を 2 つ目の設定ファイルとして足す。Service・入りも 8200 を足す。
  送り元は ESO の controller だけのまま）。最終の段の宣言は書き換えない。描画の試験は最終の段（TLS だけ）を固定し、併存の重ねは 8200 も受けることを固定する。
- 順序（手順書 `docs/operations/cloud-openbao-tls-runbook.md`）:
  1. `tls-cert.sh issue`（Secret と ConfigMap。Pod は作り直さない）
  2. 併存の重ねを当てる（OpenBao は作り直されて封印される → unseal。ESO は平文の 8200 のまま同期する）
  3. 基盤のストアを https にする（`kubectl apply -k deploy/cloud/vault/eso`）
  4. 取引ユニットのストアを https にする（取引ユニットの chart を当て直す）
  5. 全てのストアが Ready なのを確かめてから最終の段を当てて平文を閉じる（作り直し → unseal）
- unseal のあいだは ESO が同期できない（IADR-0689 決定 5。写した Secret は残る）。この段の作り直し（2 と 5）が、それに加わる止まりのすべてである。
  ストアの宛先と受け口の形が合わない時間は作らない。

## 結果

- 良い影響: ESO と OpenBao の間、オーナーの手順と OpenBao の間の通信が暗号化され、受け口の証明書の検証で宛先の取り違えも止まる。
- 悪い影響: ノードに鍵が 1 つ増える（Secret `openbao-tls`。k8s の保存時の暗号化と LUKS の下）。移るときに OpenBao の作り直しが 2 回あり、それぞれ unseal が要る。
  受け口の証明書の作り直し（1 年の期限の前）がオーナーの手順に加わる。

## 残余

1. 受け口の鍵は k8s の Secret に在る（OpenBao から供給できない「OpenBao を開ける側」の値。`secret-inventory.js` の `OPENER_SUPPLIED`）。ノードの root と、名前空間 `openbao` の Secret を読める者は読める。
2. 受け口の証明書の期限の警報は置いていない。期限は `kubectl -n openbao get secret openbao-tls -o jsonpath='{.data.tls\.crt}' | base64 -d | openssl x509 -noout -enddate` で読む（公開の部分だけ）。
3. ESO の名前空間（`external-secrets`）に NetworkPolicy を置いていない（IADR-0689 残余 4 のまま）。
4. `tls-cert.sh` は署名の後に CA の鍵の変数を空にするが、シェル（bash）はメモリ上からの消去を保証しない（鍵はプロセスが終わるまでそのメモリに残り得る。ディスク・引数・環境には出ない）。

## ［2026-11-07 追記 / IADR-0703］残余 2・3 を閉じた

- 残余 2: 受け口の証明書の期限の警報 `OpenBaoListenerCertExpiringSoon` を置いた。受け口は ESO だけが届くので測りに行かず、決定 1 の `issue`・`renew` が同じ回に作る
  CA（2 年）と受け口の証明書（1 年）の関係から、ConfigMap `openbao-ca` の最新の CA の notAfter − 365 日で導く（IADR-0703 決定 5。日数は試験が突き合わせる）。
- 残余 3: IADR-0689 残余 4 と同じく閉じた（IADR-0703 決定 3）。
