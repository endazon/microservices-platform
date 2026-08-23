---
title: 運用 Runbook — Keycloak smtpServer（SMTP リレー）の設定
type: runbook
status: draft
created: 2026-08-23
updated: 2026-08-23
author: claude
---
<!-- trace:
ids: [SC-10, SC-15, FR-05, FR-09, FR-22]
adrs: [ADR-0026, ADR-0045]
iadrs: [IADR-0197]
specs: [20260823_issue-438_keycloak-theme-and-smtp]
issues: [#438, #578, #600]
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

**実行しなくてよい場合**: 検証だけであれば、メール配信の計画 ADR の決定（開発環境では実送信しない）に従い、
捕捉用 MTA（Mailpit 等。本 runbook の対象外）を別途使う。

## なぜ realm.json に直接書かないか

`deploy/keycloak/microservices-platform-realm.json` は **--import-realm で毎回（または初回）読み込まれる
バージョン管理下のファイル**である。`smtpServer.from` / `smtpServer.user` / `smtpServer.password` は
**実環境の秘匿値または個人情報相当の値**であり、ここへ書くと平文コミットになる（メール配信の計画 ADR の決定）。

**`host` / `port` / `starttls` は秘匿値ではない**（メール配信の計画 ADR が接続の書式として確定している値：
`smtp.gmail.com` / `587` / STARTTLS 必須）。将来これらだけを realm.json へ静的に投入する余地はあるが、
**`from` / `user` / `password` を realm.json へ書くことは今後もしない**——理由は上記のとおりであり、
「実環境の値が判明したから書いてよくなる」ものではない（値の性質が変わらない限り恒久的な方針）。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | Vault へ書き込める運用者権限（`secret/msp/keycloak-smtp`）。Keycloak `admin`（realm `master`）の管理者権限 |
| 必要なツール | `kubectl`（k8s 経路）または `docker compose`（compose 経路）。`vault` CLI は不要（Pod 内 exec で足りる。[`bootstrap.sh`](../../deploy/local/vault/eso/bootstrap.sh) と同じ作法） |
| 供給元の値 | 送信元アドレス・SMTP 認証ユーザー（通常は送信元アドレスと同じ）・アプリパスワード（メール配信の計画 ADR の決定。2 段階認証が前提） |
| 所要時間の目安 | 15 分（Vault seed → ExternalSecret 同期確認 → kcadm 反映 → 疎通確認） |

## 手順（k8s 経路。`deploy/local/` の dev 環境）

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

`host` / `port` / `starttls` はメール配信の計画 ADR の確定値が既定のため、通常は上書き不要
（変える場合のみ `SMTP_HOST` / `SMTP_PORT` / `SMTP_STARTTLS` を同様に env で渡す）。

### 2. ExternalSecret を適用する（★現時点は手動。scripts/ 未配線）

[`externalsecret-keycloak-smtp.yaml`](../../deploy/local/vault/eso/externalsecret-keycloak-smtp.yaml) は
`scripts/k8s-local-up.sh` にまだ組み込まれていない（follow-up。詳細は同ファイルの参照元
[`deploy/local/vault/eso/README.md`](../../deploy/local/vault/eso/README.md)）。

```sh
kubectl apply -f deploy/local/vault/eso/externalsecret-keycloak-smtp.yaml
kubectl -n platform-infra wait --for=condition=Ready externalsecret/keycloak-smtp --timeout=60s
kubectl -n platform-infra get secret keycloak-smtp -o jsonpath='{.data.from}' | base64 -d; echo
```

最後のコマンドが空でなければ Vault → k8s Secret の同期は成立している（値そのものはここでは表示を
最小限にとどめ、`password` キーは確認しない）。

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
   `smtpServer.host` が `smtp.gmail.com` であること（`password` は出力に含まれないことが Keycloak の
   既定挙動——含まれていたら管理コンソールから目視確認に切り替える）。
2. Keycloak 管理コンソール → Realm settings → Email → **Test connection** が成功する。
3. パスワードリセット画面を実運用アカウントで申請し、リセットメールが着信する。

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
