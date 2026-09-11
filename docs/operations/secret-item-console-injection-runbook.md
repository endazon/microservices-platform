---
title: 運用 Runbook — 画面が使えないときに秘密情報を 1 項目だけコンソールから投入する
type: runbook
status: draft
author: claude
created: 2026-09-11
updated: 2026-09-11
---
<!-- trace:
ids: [SC-22, SC-06, SC-15, FR-05, NFR-11, NFR-18]
adrs: [ADR-0007, ADR-0032, ADR-0040, ADR-0042, ADR-0095]
iadrs: [IADR-0094, IADR-0096, IADR-0097, IADR-0098, IADR-0099, IADR-0332, IADR-0433]
specs: [20260911_issue-1411_sc22-console-fallback-and-bff-vault-write]
issues: [#310, #438, #1102, #1411, planning#599]
-->

# 運用 Runbook: 画面が使えないときに秘密情報を 1 項目だけコンソールから投入する

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
> 本書は**退避手段**を正式の手順として定めるものである。**既定の投入面は製品の画面**であり、
> 計画の裁定（trace ブロック参照）がそう定めている。**本書を使うのは、その画面が使えないときだけ**である。
>
> 🔴 **本書は 2026-09-10 の事故から生まれた。** その日、鍵 1 本を差し替えるために正規手順を実行すると
> **稼働中の OIDC / DB の秘密まで既定値で上書きされる**ことが分かり、**手順書に無い回避策**
> （鍵 1 本だけを狙った `kubectl apply`）がその場で判断された。**退避手段が無かったのではなく、
> 書かれていなかった。** 本書はその欠落を埋める。
>
> **値そのものは本書にもリポジトリのどこにも置かない。**

## この手順を実行する条件（いつ走らせるか）

次の **すべて** に当てはまるときだけ実行する。

- 秘密情報を **1 項目だけ**投入・更新する必要がある。
- **製品の画面から投入できない。** 画面が未実装である／落ちている／到達できない、のいずれかである。
- 対象の項目が **`deploy/bootstrap/sc22-secret-items.json` の `items[]` に載っている**。

**実行してはいけない場合**:

- 🔴 **`excluded[]` の項目を「回す」目的で実行してはならない。** 稼働中のデータストアが既存の
  パスワードで初期化済みであり、**Vault 側だけ書き換えると認証が壊れる。** 同ファイルの `reason` を読むこと。
- 🔴 **`deferred[]` の項目（認証基盤のクライアントシークレット群）を単独で書き換えてはならない。**
  **認証基盤側の宣言と同値でなければならず、片側だけ書くと認証が静かに壊れる**（トークン端点が
  `invalid_client` を返し続けるが、画面は認可の手前までは進むため気づきにくい）。
- 環境を新しく立ち上げるとき。それは退避ではなく**初期投入**であり、`scripts/k8s-local-up.sh` の
  通常経路が担う。

## 🔴 一括再投入（`bootstrap.sh`）を既定の手順にしない

`deploy/local/vault/eso/bootstrap.sh` は **28 項目すべてを再投入する**。
env で値を渡さなかった項目は**既定値（多くは開発用の固定文字列か空文字）で上書きされる**。

- **稼働中の認証基盤・データベース・ブローカの資格情報がその既定値に置き換わる。**
- ExternalSecret は上書き後の値を同期するため、**投入したかった 1 項目以外が同時に壊れる。**

**したがって、1 項目を直すために `bootstrap.sh` を実行しない。** 本書は**その項目のパスだけ**を書く。

> `bootstrap.sh` が正しいのは「何も入っていない環境を立ち上げるとき」だけである。
> **既に動いている環境に対しては、一括再投入は退避手段ではなく破壊操作である。**

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | 対象クラスタへの `kubectl exec`（`platform-infra` 名前空間）と、対象名前空間の Secret / ExternalSecret への読み書き |
| 必要なツール | `kubectl` のみ。**ホストに `vault` CLI は不要**（すべて Vault Pod 内で実行する） |
| 前提の状態 | Vault と External Secrets Operator が稼働している（`VAULT=1 ESO=1` で立ち上がった環境） |
| 所要時間の目安 | 1 項目あたり 5〜10 分（記録を残す時間を含む） |

**開発環境の Vault はインメモリである。** Pod が再起動すると投入した値は消える。
その場合は本手順ではなく通常の立ち上げ経路をやり直す。

## 手順

### 0. 記録を残すことを先に決める

🔴 **この手順は監査ログに乗らない。** 画面を経由しないため、「誰がいつどの項目を更新したか」が
**どこにも残らない**。**手順 5 で必ず記録する。** 先にそれを決めてから始めること
（作業を終えた後で思い出す形にすると、忙しい日ほど残らない）。

### 1. 対象の項目とプロパティを確かめる

```sh
# 対象が allowlist に載っていること・どのプロパティを書くのか・同期先の Secret はどれかを読む
cat deploy/bootstrap/sc22-secret-items.json
```

確かめること:

- 書こうとしているプロパティが、その項目の `properties` に**載っている**こと。
- `notWritable` に**載っていない**こと。🔴 **載っているものを書くと構成が消えるか、対で書くべき
  相手と食い違う。**
- 同期先の `targetSecret`（名前と名前空間）。手順 3・4 で使う。

### 2. 値を端末の履歴に残さずに読み込む

```sh
# -s = 画面へ表示しない。read はシェル履歴に値を残さない。
read -rs -p "value: " SECRET_VALUE && echo
```

🔴 **値をコマンドラインの引数に書かないこと。** シェル履歴・`ps` の出力・スクロールバック・
画面共有のいずれにも残る。**本手順は最後まで値を画面へ出さない。**

### 3. その項目のパスだけを書く

プロパティ 1 つだけを更新する（**同じ KV の他のプロパティに触らない**）。

```sh
kubectl -n platform-infra exec -i deploy/vault -- sh -c '
  export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"
  vault kv patch -method=patch secret/msp/llm-provider-credentials anthropic-api-key=-
' <<EOF
$SECRET_VALUE
EOF
```

- `secret/msp/llm-provider-credentials` と `anthropic-api-key` を、手順 1 で確かめた**実際の
  パスとプロパティ名**に置き換える。
- `キー名=-` は**値を標準入力から読む**書き方である。引数に値を置かないためにこれを使う。
- 🔴 **`vault kv put` を使わないこと。** `put` は **KV 全体を置き換える**ため、
  同じ KV に同居する他のプロパティ（設定値や、まだ投入していない鍵）が**消える**。
  `patch` は指定したプロパティだけを差し替える。
- KV そのものがまだ存在しない場合に限り `patch` は失敗する。そのときは `put` で**その KV の
  全プロパティを明示して**作る（欠けたプロパティは空になる、と理解した上で行う）。

書き終えたら値を捨てる。

```sh
unset SECRET_VALUE
```

### 4. 同期を促す

ExternalSecret の既定の同期間隔は 1 時間である。待たずに反映させる。

```sh
kubectl -n microservices-platform annotate externalsecret llm-provider-credentials \
  force-sync="$(date +%s)" --overwrite
```

名前空間と ExternalSecret 名は手順 1 の `targetSecret` に合わせる
（`platform-infra` 側の項目なら `-n platform-infra`）。

🔴 **Secret が更新されても、環境変数として読んでいる Pod は古い値を持ったままである。**
消費側を入れ替える。

```sh
kubectl -n microservices-platform rollout restart deploy/llm-gateway
kubectl -n microservices-platform rollout status  deploy/llm-gateway --timeout=180s
```

消費側の名前は、その Secret を `secretKeyRef` で読んでいる Deployment である
（`deploy/helm/` の values で辿れる）。**読み手がいない項目もある** —— その場合はこの段を飛ばす。

### 5. 使ったことを記録する（**省略しない**）

🔴 **コンソール操作は監査ログに乗らない。** 記録しなければ、**誰がいつ秘密を差し替えたかを
後から辿る手段が 1 つも無い。**

**記録先は #1411 へのコメント**とする。次の 4 点を書く。**値は書かない。**

- 実施日時（タイムゾーンを添える）
- 実施者
- 書いた項目とプロパティ名（例: `msp/llm-provider-credentials` の `anthropic-api-key`）
- **画面を使わなかった理由**（未実装／到達不能／障害など）

> **この記録先は暫定である。** 計画側に「退避手段を使ったことを残す手段」の設計が残っており、
> それが決まるまでの置き場として issue コメントを使う。
> **設計が来たらこの節を差し替える**（そのとき本書に日付つきの追記を入れる）。

## 確認（この手順が成功したと言える条件）

**🔴 値を端末へ出さない。長さだけを見る。**

```sh
# 1. 同期が Ready であること
kubectl -n microservices-platform get externalsecret llm-provider-credentials \
  -o jsonpath='{.status.conditions[?(@.type=="Ready")].status}{"\n"}'

# 2. 同期先 Secret の当該キーが空でないこと（長さだけ。値は表示しない）
kubectl -n microservices-platform get secret llm-provider-credentials \
  -o jsonpath='{.data.anthropic-api-key}' | base64 -d | wc -c

# 3. 他のキーが巻き込まれていないこと（キー名だけを並べる。値は出さない）
kubectl -n microservices-platform get secret llm-provider-credentials \
  -o jsonpath='{range .data.*}{"x"}{end}{"\n"}' ; \
kubectl -n microservices-platform get secret llm-provider-credentials \
  -o go-template='{{range $k,$v := .data}}{{$k}}{{"\n"}}{{end}}'
```

成功と言える条件:

- 1 が `True` を返す。
- 2 が **投入した値の長さと一致する**（0 でないことだけでは不足である —— 空文字を書いても
  `True` にはなる。**長さを自分の知っている値と照合する**）。
- 3 のキー一覧が**投入前と同じ**である（数も名前も減っていない）。
- 消費側を入れ替えた場合、そのサービスが `Ready` になり、機能が回復している。

🔴 **「ExternalSecret が Ready」だけでは成功ではない。** 空の値でも同期は成功する。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| `vault kv patch` が「no value found at ...」で失敗する | その KV がまだ存在しない（Vault Pod が再起動してインメモリの中身が消えた等） | 手順 3 の但し書きどおり `put` で KV ごと作る。**消えているのがその 1 項目だけとは限らない** —— 他の項目も消えているなら退避ではなく立ち上げ直しである |
| `vault kv patch` が 403 を返す | トークンが root ではない／policy に `patch` が無い | Vault Pod の env にある開発用 root トークンを使っているか確かめる。製品の画面経由の権限とは別物である |
| ExternalSecret が `Ready=False` のまま | `ClusterSecretStore` の認証が切れている／パスの綴りが違う | `kubectl describe externalsecret <name>` の `Events` を読む。パスは `deploy/bootstrap/sc22-secret-items.json` の `vaultPath` と一致させる |
| Secret は更新されたが挙動が変わらない | 消費側 Pod が古い環境変数を持ったまま | 手順 4 の `rollout restart` を行う |
| 同じ KV の別のキーが消えた | `put` を使ってしまった | **元の値を持っていれば書き戻す。持っていなければ、そのキーの供給元（発行元のサービス・認証基盤の宣言）から取り直す。**この事故は記録に残す（手順 5） |
| 認証が壊れた（トークン端点が `invalid_client`） | `deferred[]` の項目を単独で書き換えた | 認証基盤側の宣言と同値へ戻す。**本書の対象外の操作であり、単独では直せない** |

## 記録

**手順 5 のとおり #1411 へコメントする。** 実施日時・実施者・項目とプロパティ名・画面を使わなかった理由。
**値・値の長さ・値の一部は書かない。**

## 限界（この手順で担保できないこと）

- 🔴 **本手順は稼働環境で実測していない。** 本書を起草した作業は、クラスタにも Vault にも
  一度も触れていない（起票時の作業条件）。**「この手順で本当に直る」ことは、次に退避が必要に
  なった運用の場ではじめて確かめられる。** 食い違いが出たら本書を直すこと。
- **本手順は監査にならない。** 記録は人が書く前提であり、**書かなければ残らない。**
  監査ログに乗るのは製品の画面を経由した投入だけである。
- **本手順は回転（ローテーション）の手順ではない。** 秘密を「新しい値に差し替える」ことはできるが、
  **古い値を無効化する**のは発行元（外部サービス・認証基盤）の仕事であり、本書の射程の外である。
- **`excluded[]` の項目は本手順でも扱えない。** 扱えないことが設計であり、制限ではない。
