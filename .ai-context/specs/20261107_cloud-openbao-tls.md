---
title: 作業仕様書 — クラウドの秘匿管理（OpenBao）への通信を TLS にし、ESO とオーナーの手順で受け口の証明書を検証する（平文の口を閉じる）
type: spec
status: in-progress
related_ids: [NFR-16, NFR-05, NFR-18, IADR-0696, IADR-0689, IADR-0693, IADR-0682, IADR-0525]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - 20261106_2471_cloud-openbao-eso.md
  - 20261107_cloud-keycloak-tls.md
---

# 作業仕様書 — クラウドの OpenBao の受け口を TLS にする

> 着手 2026-11-07（develop `3d36d5ee9`）。判断の記録は IADR-0696（新）と IADR-0689 の追記（決定 3・残余 3）。
> 稼働中のクラスタには何もしない。秘密の値は扱わない（値を作るのはスクリプトで、オーナーが流す）。

## 起点

- 要求 **NFR-16**（通信の暗号化）・**NFR-05**（秘匿は OpenBao）・**NFR-18**（統制）。
- 所有者の裁定（2026-11-07）: 「OpenBao も TLS にする」。2026-11-06 の裁定（OpenBao の受け口は平文＋NetworkPolicy。IADR-0689 決定 3）を改める。
  同じ裁定の「capability を外す」（基盤の Pod の権限）は別の作業（別の PR）が持つ。本作業は `deploy/cloud/vault/`・OpenBao の Deployment／Service・ESO・
  スクリプト・手順書に絞る（`deploy/cloud/infra/*.yaml`・`deploy/cloud/observability/` の securityContext には触れない）。
- 前提: IADR-0689（OpenBao・ESO・PKI `pki_cloud`・unseal・root の失効）、IADR-0682（秘密を引数・環境・履歴・ディスクに置かない）、IADR-0693（Keycloak の TLS の併存の段の型）。

## 現状（develop `3d36d5ee9` で読んだ）

| 場所 | 現状 |
| --- | --- |
| `deploy/cloud/vault/openbao.yaml` | listener は `0.0.0.0:8200`・`tls_disable = true`。`api_addr` は http。Pod の `BAO_ADDR=http://127.0.0.1:8200`。probe は HTTP。Service は 8200 |
| `deploy/cloud/vault/networkpolicy.yaml` | 入りは ESO の controller の 8200 だけ |
| `deploy/cloud/vault/eso/*.yaml` | SecretStore 2 つ・VaultDynamicSecret 4 つが `http://openbao.openbao.svc:8200`（CA の指定なし） |
| `deploy/cloud/vault/lib.sh` の `ob_exec` | `kubectl exec … -- sh -c` で Pod の中の bao を使う。宛先は Pod の env（平文のループバック）任せ |
| 取引ユニットの chart（`values-cloud.yaml`） | `externalSecrets.openbaoStore.server: http://openbao.openbao.svc:8200`（CA の指定の口が無い） |

## 母集合（規則 9: 誤りの側の字面で走査した）

- MSP: `http://openbao`・`openbao.openbao`・`8200`・`tls_disable`・`BAO_ADDR` を、`deploy/`・`docs/operations/`・`scripts/`・`.github/workflows/`・`.ai-context/adr/` で走査した。
  - 直すもの: `openbao.yaml`（listener・api_addr・env・port・probe・Service）・`networkpolicy.yaml`（入りのポート）・`eso/*.yaml` の 6 つのストア・`lib.sh`（`ob_exec`）・
    `openbao-scripts.test.sh`・`scripts/deploy-cloud.test.js`（`vaultProblems` の 8200）・`scripts/scripts.repo.test.js`（ストアの宛先の固定）・
    `deploy/cloud/README.md`（§3・§4・§6）・`docs/operations/cloud-openbao-init-unseal-runbook.md`・IADR-0689（決定 3・残余 3）。
  - 参照の突き合わせ: `secret-inventory.js` が新しい Secret `openbao/openbao-tls` を「区分が未定」で止めた（期待どおり）。区分 3・供給元は名指しの `tls-cert.sh` とした。
  - 除外: `deploy/local/**`（ローカルの PoC の秘匿管理。クラウドだけを変える）・`src/**` の試験の宛先（`vault.test:8200`）・`.ai-context/specs/`（凍結）・
    `vault-restore-drill.sh`（イメージの名前だけ）。CI（`ci.yml`）は `openbao-scripts.test.sh` を呼ぶだけで宛先を持たない。
- 取引ユニット: `openbaoStore`・`http://openbao` を `deploy/helm/`・`.github/workflows/`・`docs/operations/`・`scripts/` で走査した。
  ストアは chart の 1 つ（`templates/external-secrets.yaml`）、宛先は `values-cloud.yaml` の 1 か所、CI の描画の確かめは `helm.yml` の 1 段。別の PR（取引ユニット）が持つ。

## 実測（手元の使い捨ての OpenBao 2.7.1。稼働のクラスタには触れない）

1. listener に証明書・鍵を与え `tls_disable` を書かなければ TLS だけで受ける。平文の要求には `Client sent an HTTP request to an HTTPS server.` を返し、CLI は失敗する。
2. 別の CA を `BAO_CACERT` に与えると `x509: certificate signed by unknown authority` で届かない。古い CA と新しい CA を連ねた束なら、どちらの証明書でも届く。
3. **SIGHUP で受け口の証明書を読み直す**（`OpenBao reload triggered`）。封印の状態は変わらない（開けたまま）。
4. `-config` を 2 つ与えると listener が足し合わされる（TLS の 8443 と平文の 8200 を同時に受ける）。
5. OpenBao のイメージは openssl を持たない（IADR-0689 実測 4）。封印のあいだは PKI で発行できない（IADR-0689 決定 3 の理由）。

## 決めたこと（理由は IADR-0696）

- **証明書の出どころ**: オーナーが手元から流すスクリプト `deploy/cloud/vault/tls-cert.sh` が、CA と受け口の証明書を手元のプロセスのメモリで作り、
  Secret `openbao/openbao-tls`（`tls.crt`・`tls.key`・`ca.crt`）と ConfigMap `openbao-ca`（基盤の 2 つの名前空間と取引ユニットの名前空間）に置く。
  **CA の鍵は署名の後に捨てる**。作り直し（`renew`）は CA ごと作り直し、CA の併記 → SIGHUP → 古い CA を外す、の順で ESO が届かない時間を作らない。
- 受け口は TLS の **8443** だけ（TLS 1.3 以上）。平文の 8200 を閉じる。移る間だけ、併存の重ね `deploy/cloud-transition/openbao-tls-coexist` が 8200 を足す。
- ESO の 6 つのストアは `https://openbao.openbao.svc:8443` と `caProvider`（ConfigMap `openbao-ca` の `ca.crt`）。検証を外す指定は使わない。
- オーナーの手順（`ob_exec` を使う全部）は、Pod の中でも `BAO_ADDR=https://127.0.0.1:8443`・`BAO_CACERT=/openbao/tls/ca.crt` を明示し、検証を外す env を消す。

## 受け入れ基準

| # | 基準 | 確かめ |
| --- | --- | --- |
| AC1 | OpenBao の受け口は TLS の 8443 だけ（平文の listener・8200 のポートが無い・probe は HTTPS・Pod の中の宛先も https と CA）。Service と入りも 8443 だけ | `scripts/deploy-cloud.test.js` OT1（修正を外すと赤） |
| AC2 | ESO の全てのストア（SecretStore 2・VaultDynamicSecret 4）は `https://openbao.openbao.svc:8443` で、CA を ConfigMap `openbao-ca` で検証する。検証を外す指定が無い | 同 OT2・`scripts/scripts.repo.test.js` |
| AC3 | 併存の重ねは 8443 と 8200 を受け（送り元は ESO の controller だけ）、最終の段の判定では落ちる。重ねは openbao の名前空間の外を描かない | 同 OT3 |
| AC4 | 実物の OpenBao 2.7.1 を TLS だけで起こし、init・unseal・configure・seed・gen・put・root-token が全部 TLS で通る。平文の要求は失敗し、別の CA では手順が届かない | `openbao-scripts.test.sh` |
| AC5 | `tls-cert.sh renew` は CA を併記してから受け口を読み直させ（封印しない）、新しい CA で届くことを確かめてから古い CA を外す。途中で落ちても併記のまま流し直せる | 同 |
| AC6 | 受け口の鍵は kubectl の引数・手元の作業場・ConfigMap に出ない | 同 |
| AC7 | `secret-inventory.js --strict` が全件で通る（`openbao/openbao-tls` は区分 3・供給元 `tls-cert.sh`） | `node deploy/cloud/secret-inventory.js --strict`・`scripts.repo.test.js` |
| AC8 | 適用の順序（MSP の併存 → 基盤のストア → 取引ユニットのストア → 平文を閉じる）を手順書と両方の PR 本文に書く | `docs/operations/cloud-openbao-tls-runbook.md` |

## 変更しないもの

- `deploy/cloud/infra/*.yaml`・`deploy/cloud/observability/`（別の作業）。ローカル（`deploy/local/**`）の秘匿管理。PKI `pki_cloud` と、それが出す基盤の証明書。
- init・unseal・root の失効の順序（IADR-0689 決定 1・2）。TLS の証明書は OpenBao を起こす前に置くので、この順序の前に 1 段が足されるだけ。
