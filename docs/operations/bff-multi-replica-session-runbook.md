---
title: 運用 Runbook — BFF を 2 レプリカにして、セッション Cookie をどちらの Pod でも復号できることを測る
type: runbook
status: draft
author: claude
created: 2026-10-04
updated: 2026-10-04
---
<!-- trace:
ids: [NFR]
adrs: [ADR-0032]
iadrs: [IADR-0251, IADR-0273, IADR-0316, IADR-0427]
specs: [20261004_1534_bff-multi-replica-session-cookie, 20260926_issue-1550_live-script-opt-in]
issues: [#1534, #439, #1389, #1550]
-->

# 運用 Runbook: BFF を 2 レプリカにして、セッション Cookie の相互復号を測る

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。** 設計の背景は
> [BFF セッション設計 実装ガイド](../authz/bff-session-design.md) §6（複数レプリカで動かすときの注意）にある。
>
> 🔴 **本書を実行するのは利用者（クラスタの持ち主）だけである。** 本書を書いた AI は稼働クラスタに 1 度も触れていない。
> **本書のコマンドと期待値は、リポジトリのコード・チャート・`helm template` の描画から導いたものであって、稼働での実測ではない。**
> 期待値と違う結果が出たら、**期待値に合わせて読み替えず、出た値をそのまま記録する**（§8）。

## この手順を実行する条件（いつ走らせるか）

- #1534 の稼働側の受け入れ基準（同じ Cookie で 20 回以上 `/bff/auth/me` が全部 200・両方の Pod で測れたこと／Pod を 1 つずつ作り直しても 200 が続くこと）を取るとき。**1 回でよい。**
- BFF のセッションの置き場（Redis の接続先・鍵リングの永続化先・アプリ名）を変えた後。
- 本番像（BFF を HPA が最小 2 で所有する配備）へ出す前に、同じ版で 1 度測っておきたいとき。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | 名前空間 `microservices-platform` で helm リリース `msp` を `upgrade` できること、`hpa` / `pods` の `get`、`pods/portforward`、`deploy` の `rollout restart`（§5 だけ）。名前空間 `platform-infra` の `deploy/redis` へ `exec`（鍵リングの件数を読むだけ。できなければ「未測定」と出る）。`cert-manager` の `secret/local-edge-root-ca` の読み取り（エッジの TLS を検証するため） |
| 必要なツール | `kubectl`（対象クラスタの context）・**`helm`（そのリリースを入れたのと同じメジャー版**。`scripts/k8s-local-up.sh` を走らせたもの）・`node` 22 |
| チェックアウトの版 | **稼働に入っている版と同じコミット**。検査器は upgrade の前に「稼働のマニフェスト」と「チェックアウトの描画」を突き合わせ、1 文書でも違えば止まる（版のずれを稼働へ押し込まないため） |
| 試験専用の利用者 | §2 で作る。**realm 宣言に在る利用者（`developer` など）は使わない**（検査器が大小を無視して拒否する） |
| シェル | bash の書き方である（PowerShell では動かない） |
| 所要時間の目安 | 約 15 分（rollout の待ちを含む。§5 を足すと約 25 分） |

---

## 1. 何が共有されていなければならないか（前提）と、壊れたときの見え方

ブラウザが持つ Cookie は、**セッションキーを DataProtection で保護したもの**である（トークンは入っていない）。
どの Pod でも受け入れられるには、次の **2 つ**が Pod の間で共有されていなければならない。

| 共有するもの | 置き場（コードの事実） | 片方の Pod にしか無いと |
| --- | --- | --- |
| Cookie を保護する鍵（鍵リング） | Redis の鍵 `bff:dataprotection-keys`。アプリ名 `microservices-platform-bff` で分離する（`src/platform/backend/Bff/Platform.Bff/Foundation/Session/BffSessionExtensions.cs`） | 発行していない Pod が Cookie を**復号できない** → 401 |
| セッション本体（トークン類） | 同じ Redis（`RedisTicketStore`）。接続先はコード既定の `redis:6379`（`BffSessionOptions.cs`。配備は上書きしない） | 復号はできても**セッションが見つからない** → 401 |

**壊れたときの見え方**（検査器が出す失敗の文言と対応する）:

| 観測 | 読み方 |
| --- | --- |
| 片方の Pod だけ同じ Cookie で 401、もう片方は 200 | 鍵リングかセッションストアが共有されていない（**この 2 つは応答では区別できない**。§1.1 の件数で切り分ける） |
| 両方の Pod で 401（ログイン直後から） | Pod の共有の問題ではない。ログインが完了していない、Cookie 名の不一致（`BFF_SESSION_COOKIE`）、Redis の再起動でセッションが消えた、のいずれか |
| 改ざんした Cookie が 200 | Cookie の保護を検証していない。**陽性の 200 は何も証明しない**（停止して記録する） |
| §5 の作り直し後だけ 401 | 鍵が Pod のメモリにしか無く、Redis へ永続化されていない |

🔴 **ログでは判定できない。** BFF のログ水準は `Microsoft.AspNetCore: Warning`（`Platform.Bff/appsettings.json`）であり、
認証の失敗や復号の失敗の情報ログは既定では出ない。**判定は応答コードで行う。**

🔴 **ローカルの Redis は永続化していない**（`deploy/local/infra/redis.yaml` は `redis:7-alpine` を素で起動する）。
Redis が作り直されると鍵リングもセッションも消え、**全 Pod で既存の Cookie が 401 になる**。これは複数レプリカの欠陥ではない
（測っている最中に Redis が作り直されたら、その回は捨てて取り直す）。

### 1.1 鍵リングの件数（検査器が自動で読む。手で見るなら）

```bash
kubectl -n platform-infra exec deploy/redis -- redis-cli LLEN bff:dataprotection-keys
```

ログインした後に **1 以上**であること。0 なら鍵は Redis へ書かれていない（共有されていない）。

---

## 2. 試験専用の利用者を作る

1. 認証基盤の管理コンソールで realm `platform` を開き、利用者を 1 人作る。
   - 利用者名は **ASCII だけ・`@` を含まない**・realm 宣言（`deploy/keycloak/*-realm.json` の `users[]`）に**無い**名前にする（例 `bff-replica-probe`）。
   - 「資格情報」でパスワードを設定し、**「一時的」をオフ**にする（オンだと初回ログインでパスワードの変更を求められ、検査器はそこで止まる）。
2. **TOTP は検査器が初回ログインで登録する**（realm は TOTP の登録を既定の必須アクションにしている）。
   登録したシークレットは `verify-oidc-edge-flow.sh` と同じ置き場（`${OIDC_TOTP_STATE_DIR:-${TMPDIR:-/tmp}}/msp-verify-oidc-totp-platform-<利用者名>.secret`、権限 600）へ保存され、2 回目以降はそこから読む。
   別の端末で走らせるときは `OIDC_TOTP_SECRET` で渡す。
3. 🔴 **ログインを何度もやり直さない。** realm は失敗 5 回で一時ロックする。パスワードの誤りで止まったら、直してから 1 回だけ走らせる。

---

## 3. 計画を確かめる（稼働クラスタに触れない）

```bash
node scripts/check-bff-multi-replica-session.js --plan
```

期待する出力（`helm template` の差分が 1 行だけ）:

```text
[check-bff-multi-replica-session]   - replicas: 1
[check-bff-multi-replica-session]   + replicas: 2
[check-bff-multi-replica-session] OK: values（deploy/local/values-local.yaml）に replicas: 2 を重ねた描画の差分は bff-service の 1 行だけ
```

**なぜこの形か（チャートの事実）**:

- レプリカ数の鍵は `services.bff.replicas`（`deploy/helm/microservices-platform/values.yaml`。既定 1）。
- 🔴 **上書きの values にリスト（`extraEnv` など）を書かない。** Helm はリストを**置換**する。
  `services.bff.extraEnv` に 1 件だけ書いた上書きで描くと、既定の env 36 件（72 行）が消える（#1389 と同じ形。チェックアウトの `deploy/local/values-local.yaml` で描いて実測）。
  env を足すなら `extraEnvAppend` を使う（本手順では env を変えない）。
- 上書きは**マップの 1 キーだけ**（`services: {bff: {replicas: 2}}`）なので、他の値は元の values のまま残る。
  `--set services.bff.replicas=2` と描画は同一である。
- 🔴 `scaling.enabled: true` かつ `scaling.services` に `bff` がある配備（**本番像の values**）では、Deployment は `replicas` を持たず HPA（最小 2）が所有する。
  そこへ `replicas` を足しても描画は変わらない。検査器は HPA を見つけたら helm を触らず、居る Pod で測る。

---

## 4. 実行する

```bash
export BFF_PROBE_USERNAME=bff-replica-probe     # §2 で作った利用者名
read -rs -p 'password: ' BFF_PROBE_PASSWORD; echo; export BFF_PROBE_PASSWORD   # 値を画面と履歴に残さない
node scripts/check-bff-multi-replica-session.js --live
```

| 環境変数 | 既定 | 用途 |
| --- | --- | --- |
| `EDGE_URL` | `https://localhost` | ログインに使うエッジ（`verify-oidc-edge-flow.sh` と同じ） |
| `BFF_SESSION_COOKIE` | `__Host-msp-session` | セッション Cookie 名（BFF の既定。同上） |
| `HELM` | `helm` | 使う helm の実体 |

検査器がすること（順に。どこで止まっても §6 の戻しは必ず走る）:

1. HPA が `bff-service` を所有しているかを見る。所有していれば 2〜3 を飛ばす。
2. `helm get values msp` で**現在の values を退避**し（一時ファイル・権限 600）、
   ①稼働のマニフェストとチェックアウトの描画が一致すること ②上書きの差分が 1 行であること を確かめる。どちらかが崩れたら**何も変えずに止まる**（exit 2）。
3. `helm upgrade msp … -f 退避した values -f 上書き` → `kubectl rollout status`。**`kubectl scale` は使わない**（helm の持つ値と稼働が食い違う）。
4. Ready の Pod が 2 つ以上あることを確かめ、試験利用者でエッジ経由のログインを通す（Cookie の値は画面へ出さない）。
5. **Pod ごとに port-forward** し、同じ Cookie で `/bff/auth/me` を各 10 回（`--per-pod N` で変える。2 Pod で合計 20）、
   1 文字だけ変えた Cookie を各 1 回送る。**どの要求がどの Pod に当たったかは構成で決まる**ので、アクセスログで振り分けを確かめる必要は無い。
6. 判定（すべて満たして合格）: 各 Pod で同じ Cookie が全部 200・`/me` の利用者名が試験利用者・改ざんした Cookie が 401。
7. §6 の戻し。

期待する出力（合格のとき。Pod 名は環境で変わる）:

```text
[check-bff-multi-replica-session]   bff-service-xxxxx-aaaaa: 同じ Cookie → 200,200,200,200,200,200,200,200,200,200 ／ 改ざん → 401
[check-bff-multi-replica-session]   bff-service-xxxxx-bbbbb: 同じ Cookie → 200,200,200,200,200,200,200,200,200,200 ／ 改ざん → 401
[check-bff-multi-replica-session] [前提] Redis の bff:dataprotection-keys: 1 件
[check-bff-multi-replica-session] OK: 同じ Cookie をすべての Pod が同じ利用者として受け入れ、改ざんした Cookie はすべての Pod が 401 で拒んだ。
```

終了コード: 0=合格 / 1=不合格（または戻しの失敗） / 2=前提未整備・停止条件 / 3=`--live` の指定なし。

## 5. Pod を作り直しても同じ Cookie が通るか（任意）

```bash
node scripts/check-bff-multi-replica-session.js --live --restart
```

§4 の測定の後に `kubectl rollout restart deploy/bff-service` を行い（既定の RollingUpdate なので 1 つずつ入れ替わる）、
**新しく起きた Pod だけ**で同じ Cookie を測り直す。新しい Pod は鍵リングを Redis から読むので、ここで 401 なら鍵はメモリにしか無かった。

`rollout restart` は Pod テンプレートに `kubectl.kubernetes.io/restartedAt` の注釈を残す（helm はこれを消さない）。害は無いが、次の `helm upgrade` の差分に出ても驚かないこと。

---

## 6. 戻し方

**検査器は終了時に必ず戻す**（合格・不合格・途中の失敗・Ctrl-C のいずれでも）: 退避した values だけで `helm upgrade` し直し、`rollout status` を待つ。
戻しに失敗したら `🔴 戻しに失敗した` と退避ファイルの場所を出して exit 1 で終わる。そのときは手で戻す:

```bash
helm history msp -n microservices-platform          # replicas=2 にした版の 1 つ前の REVISION を探す
helm rollback msp <その REVISION> -n microservices-platform
kubectl -n microservices-platform rollout status deploy/bff-service --timeout=300s
kubectl -n microservices-platform get deploy bff-service -o jsonpath='{.spec.replicas}{"\n"}'   # 1 を期待
```

🔴 **`kubectl scale --replicas=1` で戻さない。** helm の値は 2 のまま残り、次の `helm upgrade` が 2 へ戻す。

## 7. 停止条件（ここで止めて記録する）

- 検査器が **exit 2** で止まった（版のずれ・上書きの差分が 1 行でない・既に `replicas` が上書きされている・利用者やパスワードの指定漏れ・エッジ CA が読めない）。**値を合わせ込んで再実行しない**。出た文言を記録する。
- ログインが失敗した（パスワード誤り・必須アクション）。**再試行を重ねない**（一時ロックの計数を消費する）。
- 改ざんした Cookie が 200 を返した（§1 の表）。それ以上の測定は意味が無い。
- 測定中に Redis かエッジが作り直された。その回は捨てる。
- 戻しが失敗した（§6 の手で戻すまで、ほかの作業へ進まない）。

## 8. 記録表（#1534 に貼る）

| 項目 | 値 |
| --- | --- |
| 実施日時・実施者 | |
| チェックアウトのコミット | `git rev-parse --short HEAD` |
| helm の版 | `helm version --short` |
| レプリカ数を持つもの | helm の values ／ HPA（名前） |
| Pod 名と応答（同じ Cookie／改ざん） | 検査器の出力をそのまま |
| 鍵リングの件数 | 検査器の `[前提]` 行（未測定ならそう書く） |
| `--restart` の結果 | 実施した／しない。した場合は新しい Pod 名と応答 |
| 戻しの確認 | `kubectl -n microservices-platform get deploy bff-service -o jsonpath='{.spec.replicas}'` の値 |
| 期待と違った点 | そのまま書く（読み替えない） |

## 9. 後片付け

1. §2 の試験利用者を管理コンソールで削除する。BFF のセッションは Redis に残るが、次のトークン更新で認証基盤が拒むので、その時点で BFF が自分で破棄する。
2. TOTP の状態ファイル（§2 の 2）を消す。
3. 退避した values の一時ファイルは、戻しが成功していれば検査器が消している。
