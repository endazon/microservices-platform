---
title: 運用 Runbook — Keycloak smtpServer（SMTP リレー）の設定
type: runbook
status: draft
created: 2026-08-23
updated: 2026-09-03
author: claude
---
<!-- trace:
ids: [SC-10, SC-15, FR-05, FR-09, FR-22]
adrs: [ADR-0026, ADR-0045]
iadrs: [IADR-0197, IADR-0261, IADR-0332, IADR-0344, IADR-0345]
specs: [20260823_issue-438_keycloak-theme-and-smtp, 20260831_issue-1102_keycloak-smtp-externalsecret-wiring, 20260902_issue-1144_dev-mail-capture-mta, 20260902_issue-1143_reset-existence-concealment]
issues: [#438, #578, #600, #1102, #1143, #1144]
-->

# 運用 Runbook: Keycloak smtpServer（SMTP リレー）の設定

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
> 起点: **#438**（#578 が「足りないもの」として分離した項目。先行する実装 ADR の決定を引き継ぐ）。
>
> **本書は「実環境の値が供給されてから、それを安全に投入する手順」を定める。**
> **値そのものは本書にもリポジトリのどこにも置かない**（メール配信の計画 ADR の決定。CLAUDE.md 禁止事項「機密情報のコミット」）。

## この手順を実行する条件（いつ走らせるか）

- 組織のメールテナント（go-live では Google Workspace）から SMTP 接続情報（ホスト・送信元アドレス・
  アプリパスワード）が供給されたとき（初回投入）。
- 送信元アドレスの変更・アプリパスワードのローテーションが必要になったとき（再投入）。
- realm を作り直した（`--import-realm` の再インポート・PVC 削除等）ため `smtpServer` が消えたとき。

**実行しなくてよい場合**: 検証だけであれば本手順は要らない。メール配信の計画 ADR の決定（開発環境では
実送信しない）に従い、**開発環境には捕捉用 MTA が dev 既定で立っている**（`deploy/local/infra/mailpit.yaml`）。
`scripts/k8s-local-up.sh` が起動し、realm の既定の送出先もそこを指す —— **何もしなくても送出は成立し、
1 通も外へ出ない**。受信したメールは次で読む。

```sh
kubectl -n platform-infra port-forward svc/mailpit 8025:8025   # → http://localhost:8025
node scripts/check-password-reset-mail.js                       # 申請→送出→受信→本文を機械で確かめる
```

🔴 **本手順は「外部の実リレーへ向ける」ための手順である。** 実行すると、その開発環境からのメールは
**外部へ実送信される**。疎通と文面の検証が要る段階に限って行うこと（計画 ADR の同決定の但し書き）。

## なぜ realm.json に直接書かないか

`deploy/keycloak/microservices-platform-realm.json` は **--import-realm で毎回（または初回）読み込まれる
バージョン管理下のファイル**である。`smtpServer.from` / `smtpServer.user` / `smtpServer.password` は
**実環境の秘匿値または個人情報相当の値**であり、ここへ書くと平文コミットになる（メール配信の計画 ADR の決定）。

**`host` / `port` / `starttls` は秘匿値ではない**（メール配信の計画 ADR が接続の書式として確定している値：
`smtp.gmail.com` / `587` / STARTTLS 必須）。**`from` / `user` / `password` を realm.json へ書くことは今後もしない**
——理由は上記のとおりであり、「実環境の値が判明したから書いてよくなる」ものではない
（値の性質が変わらない限り恒久的な方針）。

> **［2026-09-02］realm.json には dev 既定の `smtpServer` が入っている。** 宛先は**クラスタ内の捕捉用 MTA**
> であり、`from` も `noreply@platform.localhost` という**クラスタの外では意味を持たない合成値**である。
> **秘匿値は 1 つも入っていない**（上の恒久方針と矛盾しない —— 禁じているのは**実環境の値**である）。
> 本手順を実行すると、**稼働中の realm の実行時状態だけ**が実リレーへ向く（`realm.json` は書き換えない）。
> **realm を再インポートすると dev 既定へ戻る** —— これは事故ではなく fail-safe である
> （再インポート後にうっかり実送信する状態にならない）。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | Vault へ書き込める運用者権限（`secret/msp/keycloak-smtp`）。Keycloak `admin`（realm `master`）の管理者権限 |
| 必要なツール | `kubectl`（k8s 経路）または `docker compose`（compose 経路）。`vault` CLI は不要（Pod 内 exec で足りる。[`bootstrap.sh`](../../deploy/local/vault/eso/bootstrap.sh) と同じ作法） |
| 供給元の値 | 送信元アドレス・SMTP 認証ユーザー（通常は送信元アドレスと同じ）・アプリパスワード（メール配信の計画 ADR の決定。2 段階認証が前提） |
| 所要時間の目安 | 15 分（Vault seed → ExternalSecret 同期確認 → kcadm 反映 → 疎通確認） |

## 手順（k8s 経路。`deploy/local/` の dev 環境）

### 0. 🔴 先に申請を閉じる（存在秘匿を割らないため。**省略しないこと**）

**送出先を差し替えている間、パスワードリセットの申請を開いたままにしてはならない。**
送出に失敗すると**実在する利用者名のときだけ 500** が返り、実在しない利用者名は 200 を返す ——
**その差だけで利用者名を 1 リクエストずつ列挙できる**（稼働環境で実測済み）。

閉じてしまえば、実在／非実在のどちらにも**同じ 400 と同じ本文**が返る（実測済み）。

```sh
KC_POD=$(kubectl -n platform-infra get pod -l app=keycloak -o jsonpath='{.items[0].metadata.name}')
kubectl -n platform-infra exec -i "$KC_POD" -- sh -c   '/opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 --realm master      --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null    && /opt/keycloak/bin/kcadm.sh update realms/platform -s "resetPasswordAllowed=false"'
```

> **同じ理由で、送出経路が落ちたときも閉じる。** 起動器の到達判定（`node scripts/check-stack-ready.js`）が
> 捕捉用 MTA の停止を検出したら、復旧までの間は上のコマンドで閉じておくこと。
> **閉じている間はリセットが使えない**が、**利用者名が漏れるよりはよい**（fail-closed）。


### 1. Vault へ値を投入する（Secret の値は画面や CLI 履歴に残さない）

`bootstrap.sh` は `secret/msp/keycloak-smtp` を **env 由来 or 空既定**で seed する
（[`deploy/local/vault/eso/bootstrap.sh`](../../deploy/local/vault/eso/bootstrap.sh)）。値を対話的に読み込んで
再実行する（シェル履歴に残る `export` 形は避け、`read -rs` を使う。既存の Vault OIDC 手順と同じ作法）。

```sh
read -rs SMTP_FROM;     export SMTP_FROM
read -rs SMTP_USER;     export SMTP_USER
read -rs SMTP_PASSWORD; export SMTP_PASSWORD
bash deploy/local/vault/eso/bootstrap.sh
unset SMTP_FROM SMTP_USER SMTP_PASSWORD
```

🔴 **`SMTP_HOST` は必ず明示すること。** `bootstrap.sh` の**宛先の既定はクラスタ内の捕捉用 MTA**であり、
外部の実リレーではない（メール配信の計画 ADR の決定「開発環境では実送信しない」を既定で満たすため）。
`SMTP_HOST` を渡さずに `from`/`user`/`password` だけ入れても、**メールは捕捉用 MTA に溜まるだけで外へは出ない**。

```sh
export SMTP_HOST=<供給されたホスト>   # 秘匿値ではない（計画 ADR が書式として確定している値）
```

`SMTP_PORT` / `SMTP_STARTTLS` は**宛先から導出される** —— 捕捉用 MTA 以外を指した時点で
**計画 ADR の確定値（`587` / STARTTLS 有効）が既定になる**。明示的に変えたいときだけ env で渡す。
**STARTTLS を無効のまま外部へ繋ぐ経路は作らないこと**（計画 ADR の決定。平文フォールバックを許さない）。

### 2. ExternalSecret の同期を確認する（適用は起動器が済ませている）

**ExternalSecret は手で適用しない。** 起動スクリプトが `ESO=1` のとき常時適用し、同期の完了まで
待ち合わせる（適用の並び・待ち合わせの根拠は
[`deploy/local/vault/eso/README.md`](../../deploy/local/vault/eso/README.md)）。
**§1 で値を入れ替えたあとに残るのは、同期が済んだことの確認だけである。**

```sh
kubectl -n platform-infra wait --for=condition=Ready externalsecret/keycloak-smtp --timeout=60s
kubectl -n platform-infra get secret keycloak-smtp -o jsonpath='{.data.from}' | base64 -d | wc -c
```

**最後のコマンドが 0 より大きければ**、Vault → k8s Secret の同期は成立している。
**値そのものは表示しない**（長さだけを見る。`password` キーは確認しない）。

> **同期の間隔は 1 時間である**（`refreshInterval: 1h`）。§1 の再 seed 直後に長さが 0 のままなら、
> まだ前の（空の）値を保持している。`kubectl -n platform-infra delete secret keycloak-smtp` で
> ESO に作り直させるか、次の refresh を待つ。
>
> 🔴 **長さが 0 のまま §3 へ進まないこと。** 空の `from` で `kcadm` を打つと、Keycloak は
> `Please provide a valid address` で送信を拒否し、**パスワードリセット申請が 500 になる**
> （空の `smtpServer` を持たない状態より悪い）。

### 3. 稼働中の realm へ smtpServer を反映する（kcadm。realm.json は書き換えない）

**k8s Secret の値をシェル変数へ短時間だけ読み込み、`kcadm.sh update` で直接 PATCH する。**
ファイルへ書き出さない（`kcadm.sh` は引数で受け取れる）。

```sh
KC_POD=$(kubectl -n platform-infra get pod -l app=keycloak -o jsonpath='{.items[0].metadata.name}')
KC_ADMIN_PW=$(kubectl -n platform-infra get secret keycloak-admin -o jsonpath='{.data.password}' | base64 -d)

kubectl -n platform-infra exec -i "$KC_POD" -- /opt/keycloak/bin/kcadm.sh config credentials \
  --server http://localhost:8080 --realm master --user admin --password "$KC_ADMIN_PW"

SMTP_HOST=$(kubectl -n platform-infra get secret keycloak-smtp -o jsonpath='{.data.host}' | base64 -d)
SMTP_PORT=$(kubectl -n platform-infra get secret keycloak-smtp -o jsonpath='{.data.port}' | base64 -d)
SMTP_FROM=$(kubectl -n platform-infra get secret keycloak-smtp -o jsonpath='{.data.from}' | base64 -d)
SMTP_USER=$(kubectl -n platform-infra get secret keycloak-smtp -o jsonpath='{.data.user}' | base64 -d)
SMTP_PASSWORD=$(kubectl -n platform-infra get secret keycloak-smtp -o jsonpath='{.data.password}' | base64 -d)

kubectl -n platform-infra exec -i "$KC_POD" -- /opt/keycloak/bin/kcadm.sh update realms/platform \
  -s "smtpServer.host=$SMTP_HOST" \
  -s "smtpServer.port=$SMTP_PORT" \
  -s "smtpServer.from=$SMTP_FROM" \
  -s "smtpServer.user=$SMTP_USER" \
  -s "smtpServer.auth=true" \
  -s "smtpServer.starttls=true" \
  -s "smtpServer.password=$SMTP_PASSWORD"

unset SMTP_HOST SMTP_PORT SMTP_FROM SMTP_USER SMTP_PASSWORD KC_ADMIN_PW
```

> **これは realm の「実行時状態」への変更であり、`realm.json`（バージョン管理下）には残らない。**
> §なぜ realm.json に直接書かないか のとおり、これは意図的な設計である。**realm を再インポートする
> 運用仕様書の反映手順（破壊的コース）を実行すると smtpServer は消えるため、
> その場合は本手順を再実行する。**

## 手順（docker-compose 経路。`deploy/docker-compose.yml` の dev 環境）

Vault が無い compose 経路では、値を環境変数から直接 kcadm へ渡す（Vault 相当のシークレットストアを
compose には持たないため、実行者が値を保持する時間を最短にする）。

> 🔴 **compose 経路には捕捉用 MTA が居ない。** 計画 ADR の決定は「**k3s 上に**捕捉用 MTA を置く」と
> 述べており、本リポジトリもそこに置いた（`deploy/local/infra/mailpit.yaml`）。**compose でこの手順を
> 実行すると、その時点から外部へ実送信される** —— 検証は k8s 経路で行うこと。

```sh
read -rs SMTP_FROM;     export SMTP_FROM
read -rs SMTP_USER;     export SMTP_USER
read -rs SMTP_PASSWORD; export SMTP_PASSWORD

docker compose -f deploy/docker-compose.yml exec keycloak \
  /opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 \
    --realm master --user admin --password admin

docker compose -f deploy/docker-compose.yml exec keycloak \
  /opt/keycloak/bin/kcadm.sh update realms/platform \
    -s "smtpServer.host=smtp.gmail.com" \
    -s "smtpServer.port=587" \
    -s "smtpServer.from=$SMTP_FROM" \
    -s "smtpServer.user=$SMTP_USER" \
    -s "smtpServer.auth=true" \
    -s "smtpServer.starttls=true" \
    -s "smtpServer.password=$SMTP_PASSWORD"

unset SMTP_FROM SMTP_USER SMTP_PASSWORD
```

## 確認（この手順が成功したと言える条件）

1. `kubectl -n platform-infra exec -i "$KC_POD" -- /opt/keycloak/bin/kcadm.sh get realms/platform` の
   `smtpServer.host` が供給されたホストであること（`password` は出力に含まれないことが Keycloak の
   既定挙動——含まれていたら管理コンソールから目視確認に切り替える）。

   > 🔴 **`--fields smtpServer` を付けて確認してはならない**（2026-09-02 実測）。**設定済みの realm に対しても
   > `"smtpServer" : { }` を返す** —— `--fields` の絞り込みは入れ子のマップを描かない。
   > **付けずに全体を取り、`grep -A8 smtpServer` で読むこと。** 付けたまま読むと「設定が入っていない」と
   > 誤診し、**入っているのに入れ直す**（あるいは「入れたのに効かない」と原因を取り違える）。
   > 同じ誤診が、本 runbook 以前の調査記録にも残っている。
2. Keycloak 管理コンソール → Realm settings → Email → **Test connection** が成功する。
3. 🔴 **送出が成立することを確かめてから、申請を開き直す**（§0 で閉じたものを戻す。**順序を逆にしない** ——
   送出が壊れたまま開くと、実在する利用者名だけ 500 になり利用者名が漏れる）。

   ```sh
   kubectl -n platform-infra exec -i "$KC_POD" -- sh -c      '/opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 --realm master         --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null       && /opt/keycloak/bin/kcadm.sh update realms/platform -s "resetPasswordAllowed=true"'
   node scripts/check-password-reset-mail.js   # 開閉と送出先の組・応答の同値性を機械で確かめる
   ```
4. パスワードリセット画面を実運用アカウントで申請し、リセットメールが着信する。

**捕捉用 MTA へ戻すとき**（検証が終わったら戻すこと。戻さないと以後の申請が外部へ実送信され続ける）。
**戻すときも §0 と同じ順序で** —— 先に閉じ、送出先を戻し、成立を確かめてから開く:

```sh
kubectl -n platform-infra exec -i "$KC_POD" -- /opt/keycloak/bin/kcadm.sh update realms/platform \
  -s "smtpServer.host=mailpit.platform-infra.svc.cluster.local" \
  -s "smtpServer.port=1025" -s "smtpServer.auth=false" -s "smtpServer.starttls=false"
node scripts/check-password-reset-mail.js   # 戻ったことを機械で確かめる
```

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| ExternalSecret が `Ready` にならない | Vault に値が未投入（§1 未実施）／`eso-read` policy が `secret/msp/keycloak-smtp` を含んでいない | `vault kv get secret/msp/keycloak-smtp`（Vault Pod 内）で値の有無を確認。policy は `secret/msp/*` を許可済みのため通常は該当しない |
| Test connection が認証エラー | アプリパスワードの失効・2 段階認証の設定変更 | 組織のメールテナント側でアプリパスワードを再発行し、§1 から再実行 |
| Test connection は成功するがリセットメールが届かない | 宛先ドメイン制限（go-live では適用外のはずだが、設定が残っている場合） | `smtpServer` に `envelopeFrom` 等の制限が入っていないか確認。メール配信の計画 ADR の該当決定の適用状況を確認 |
| 送信元アドレスが `noreply@` を期待して見える | 個人 Google アカウントを例外的に使っている場合の既知の制約 | メール配信の計画 ADR §結果「受け入れたリスク」参照。組織テナントへの移行までの既知の制約であり、本手順の不具合ではない |

## 記録

- 実施日・実施者・供給元（組織テナントか個人アカウントか）を監査ログ相当の記録
  （`docs/operations/operations.md` の障害対応記録、または部門の変更管理台帳）へ残す。
- **値そのもの（アドレス・パスワード）は記録に含めない。**

## 限界（この手順で担保できないこと）

- **本手順は「値を投入する」ところまでであり、「値が正しく供給され続ける」ことは担保しない。**
  アプリパスワードの失効・組織テナントへの移行は別途の運用判断が要る（メール配信の計画 ADR §結果 フォローアップ参照）。
- **送信失敗の監視（運用ダッシュボード）は本手順の対象外。** 送信基盤の死活・
  失敗率の観測は利用者向け通知機能側の実装（#600・未着手）に依存する。
  **本手順を実行しても、送信失敗が自動検知されるようにはならない。**
- **メールテナント停止時の代替**（管理者による本人確認済みリセット）は
  `UPDATE_PASSWORD` 必須アクションとして realm.json に投入済みであり、**本手順（SMTP そのものの設定）とは
  独立に機能する。** 本手順が失敗していても、代替手段は影響を受けない。
