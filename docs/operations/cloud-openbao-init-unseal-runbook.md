---
title: 運用 Runbook — クラウドの秘匿管理（OpenBao）の初期化・unseal・値の作成・root の作り直し
type: runbook
status: draft
author: claude
created: 2026-11-06
updated: 2026-11-06
---
<!-- trace:
ids: [NFR-05, NFR-18]
adrs: [ADR-0132]
iadrs: [IADR-0689, IADR-0674, IADR-0682, IADR-0525]
specs: [20261106_2471_cloud-openbao-eso]
issues: [#2471, #2401]
-->

# 運用 Runbook: クラウドの秘匿管理（OpenBao）

> **運用仕様書（[`operations.md`](operations.md)）の「秘匿管理」の下位にあたる手順書である。**
> 対象はクラウドの最小構成（単一ノードの k3s・`deploy/cloud/`）の `deploy/cloud/vault/`。ローカルの PoC の秘匿管理の手順とは別である。
>
> **状態を `draft` に留めている理由**: クラウドのノードで初期化と再起動の後の unseal をまだ記録していない（移行の門の条件 5 で行う）。

## 前提

- **手順を流すのはオーナーである。** 秘密の値は画面に出ない。エージェントは値を扱わない。
- 手元（オーナーの端末）に `kubectl`（SSH のトンネル越しに API サーバーへ届く）・`age`・`node` がある。
- age の鍵の組をオーナーが持つ。受取人（公開鍵）のファイルと、識別子（秘密鍵）のファイルである。識別子はノードに置かない。
- スクリプトは秘密の値を**引数・環境変数・ディスクに置かない**。手元では age の復号の出力をパイプで Pod の標準入力へだけ流し、
  Pod の中では標準入力で受ける。コマンド行に値は無いので、シェルの履歴にも残らない。

## 手元に残るもの（2 つのファイル）

| ファイル | 中身 | 作る手順 |
| --- | --- | --- |
| `<init>.age` | unseal の鍵（1 分割・閾値 1）と初回の root（configure の後は失効している） | §1 |
| `<operator>.age` | オーナー用の資格（AppRole。Pod の中のループバックからだけ入れる） | §3 |

- **どちらも、別々の 2 か所に保管する**（場所はリポジトリに書かない）。片方を失っても、もう片方から戻せる。
- `<init>.age` を両方とも失うと、封印を開けられない。データを捨てて初期化からやり直す（§7）。

## 1. 初期化（一度だけ）

```sh
kubectl apply -k deploy/cloud/vault
kubectl -n openbao get pods                    # Running・Ready 0/1（初期化前・封印中は Ready にならない）
bash deploy/cloud/vault/init.sh --live --recipients <受取人のファイル> --out <init>.age
```

- 出力は Pod の中から手元の `age -R` へそのまま流れ、暗号化したファイル（0600）だけができる。平文はどこにも残らない。
- 受取人で暗号化できることを先に確かめてから初期化する。途中で失敗したら書き出し先を消す。
  **初期化だけが済んで出力を受け取れなかった**（ネットワークが切れた）ときは、鍵を誰も持たない。§7 でデータを捨ててやり直す。

## 2. unseal（初期化の後と、再起動のたび）

```sh
bash deploy/cloud/vault/unseal.sh --live --init-file <init>.age --identity <識別子のファイル>
kubectl -n openbao get pods                    # Ready 1/1 になる
```

### 再起動のときに止まるもの

- 解錠の順は **LUKS → k3s → unseal → ESO の同期**である。unseal はノードの再起動と、OpenBao の Pod の作り直しのたびに要る。
- 封印のあいだ止まるもの: ESO の同期と更新・新しい値の取得・証明書の発行・値の作成（§4）。
- **止まらないもの**: ESO が既に写した Secret は k8s に残る（保存時の暗号化と LUKS の下）。Postgres・RabbitMQ・Keycloak・LLM ゲートウェイと
  取引ユニットは、unseal を待たずに起動できる。封印が止めるのは「値を新しく得る・変える」ことである。
- 封印のまま半年を越えると、証明書の作り直しが止まる（期限は 1 年）。再起動の後は、先に unseal する。

## 3. 設定（初期化の直後に一度だけ）

```sh
bash deploy/cloud/vault/configure.sh --live --init-file <init>.age --identity <識別子のファイル> \
  --recipients <受取人のファイル> --operator-out <operator>.age
```

- 初回の root を 1 回だけ使い、**最後に失効させる**（出力の「初回の root を失効させた」を確かめる）。
- 作るもの: 値の置き場（KV `cloud/`）・基盤との通信の CA（PKI `pki_cloud/`。CA の鍵は OpenBao の外へ出ない）・ESO の認証の role と policy・
  オーナー用の資格。CA の証明書（公開してよい）を ConfigMap `platform-infra-ca` に置く。
- オーナー用の資格は、もう一度は作らない（2 回目は止まる）。作り直すときは §6。

## 4. 値を作る（configure の後に一度。足すとき）

```sh
bash deploy/cloud/vault/seed-secrets.sh --live --operator-file <operator>.age --identity <識別子のファイル>
```

- DB・Keycloak・ブローカの資格情報と、取引ユニットの 3 つの機密クライアントの secret を、**OpenBao の Pod の中で**乱数から作る。
  値は Pod の外へ出ない。在る値は触らない（2 回目は何も変えない）。
- 取引ユニットだけの値を作る口（取引ユニットの手順書から呼ぶ）:

  ```sh
  bash deploy/cloud/vault/seed-secrets.sh --live --operator-file <operator>.age --identity <識別子のファイル> \
    gen cloud/ast/<名前> <キー> <バイト数>          # 指定のバイト数の乱数を標準の base64 で書く。在るキーは上書きしない
  bash deploy/cloud/vault/seed-secrets.sh --live --operator-file <operator>.age --identity <識別子のファイル> \
    gen-rsa cloud/ast/<名前> <キー> <ビット数>      # RSA の秘密鍵（PKCS#1 の PEM）。2048・3072・4096
  ```

- `gen-rsa` は 2048 ビット未満を作れない（OpenBao が拒む）。それより短い鍵が要る相手には使えない。

## 5. 突き合わせ（移行の門の条件 3）

```sh
node deploy/cloud/secret-inventory.js --strict
```

- 宣言が参照する Secret の全件を、供給元（OpenBao から写す ExternalSecret）と区分（外から持ち込むもの・クラスタの中で作るもの・TLS の鍵）に照らす。
  1 つでも供給元が無ければ止まる。表に無い名前の Secret が増えたときも止まる（区分を決めてから足す）。
- 外から持ち込む資格情報（LLM の鍵・バックアップの保管先・Discord・heartbeat）は、OpenBao から写す形が入るまで「供給元なし」と出る。

## 6. root が要る操作（root を作り直し、操作の後に失効させる）

```sh
bash deploy/cloud/vault/root-token.sh --live --init-file <init>.age --operator-file <operator>.age --identity <識別子のファイル> apply-policies
bash deploy/cloud/vault/root-token.sh --live --init-file <init>.age --operator-file <operator>.age --identity <識別子のファイル> \
  rotate-operator --recipients <受取人のファイル> --operator-out <新しい operator>.age
```

- OpenBao 2.x の root の作り直しには認証が要る。オーナー用の資格で入り、unseal の鍵を加えて作る。作った root は操作の後に失効させる。
- `apply-policies`: policy（`deploy/cloud/vault/policies/`）を改めたときに当て直す。
- `rotate-operator`: オーナー用の資格のファイルを失った・漏れた疑いのときに作り直す。古い資格はすべて消える。2 か所の保管を差し替える。

## 7. 初期化からやり直す（鍵を失ったとき）

1. OpenBao を止め、データ（PVC `openbao-data`）を消す: `kubectl delete -k deploy/cloud/vault`（名前空間ごと消える）。
2. §1 から流す。値は作り直しになる。基盤と取引ユニットの消費側（DB の利用者・ブローカの利用者・Keycloak の secret）も、新しい値で作り直す。
