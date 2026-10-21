---
title: 運用 Runbook — Alertmanager のメール受信先（近接 MTA 経由）の配備と試験警報
type: runbook
status: draft
created: 2026-10-21
updated: 2026-10-21
author: claude
---
<!-- trace:
ids: [NFR-21, SC-10]
adrs: [ADR-0006, ADR-0045, ADR-0078]
iadrs: [IADR-0571, IADR-0304, IADR-0165, IADR-0404]
specs: [20261021_2044_alertmanager-mail-receiver]
issues: [#2044, #546]
-->

# 運用 Runbook: Alertmanager のメール受信先

> **運用仕様書（[`operations.md`](operations.md)）の「監視・アラート」の下位にあたる手順書である。**
> 対象はローカル k8s（経路B）の可観測性オーバーレイの Alertmanager。compose 経路は受信先を持たない（近接 MTA が無い）。

## 何が起きるか

- Alertmanager は起動時に Secret `alertmanager-mail` のキー `to` を読み、値があれば受信先 `ops-mail` を描画して
  既定の route をそこへ向ける。メールは近接 MTA（`mail-relay:587`）へ投函され、上流（Secret `keycloak-smtp`）へ後送される。
  ローカルの既定の上流は捕捉用 MTA（Mailpit）であり、1 通も外へ出ない。
- **`to` が空・Secret が無い**ときは警告を出して「どこへも送らない」受信先で起動する（届くのは Alertmanager の画面まで）。
- **`to` がメールアドレス（カンマ区切り可）の形でない**ときは Alertmanager が起動しない（`Init:Error`）。
- 宛先の実値はリポジトリのどこにも置かない。

## 1. 宛先を投入する（どの Secret にどのキーか）

| 置き場 | キー | 値 |
| --- | --- | --- |
| OpenBao `secret/msp/alertmanager-mail`（ESO を使う環境） | `to` | 宛先のメールアドレス。複数ならカンマ区切り（例: `ops@example.com,oncall@example.com`） |
| Secret `platform-infra/alertmanager-mail`（ESO を使わない環境） | `to` | 同上 |

ESO を使う環境（値はシェル履歴に残さない）:

```sh
read -rs ALERT_MAIL_TO; echo
printf '%s' "$ALERT_MAIL_TO" | kubectl -n platform-infra exec -i deploy/vault -- sh -c \
  'export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"; vault kv put secret/msp/alertmanager-mail to=-'
unset ALERT_MAIL_TO
kubectl apply -f deploy/local/vault/eso/externalsecret-alertmanager-mail.yaml   # 起動器（OBSERVABILITY=1）も apply する
# refreshInterval（1h）を待たずに同期させる
kubectl -n platform-infra annotate externalsecret alertmanager-mail force-sync="$(date +%s)" --overwrite
kubectl -n platform-infra wait --for=condition=Ready externalsecret/alertmanager-mail --timeout=60s
```

ESO を使わない環境:

```sh
read -rs ALERT_MAIL_TO; echo
printf '%s' "$ALERT_MAIL_TO" > /tmp/alert-mail-to && chmod 600 /tmp/alert-mail-to
kubectl -n platform-infra create secret generic alertmanager-mail --from-file=to=/tmp/alert-mail-to \
  --dry-run=client -o yaml | kubectl apply -f -
rm -f /tmp/alert-mail-to; unset ALERT_MAIL_TO
```

## 2. 反映する

宛先は Pod 起動時に 1 度だけ描画される。Reloader が居れば Secret の更新で作り直されるが、
**Secret を初めて作ったときは作り直されない**ので、手で作り直す。

```sh
kubectl apply -k deploy/local/observability      # 受信先の器を含む版を当てる（初回のみ）
kubectl -n platform-infra rollout restart deploy/alertmanager
kubectl -n platform-infra rollout status deploy/alertmanager --timeout=120s
kubectl -n platform-infra logs deploy/alertmanager -c render-config
#   期待値: 「受信先 ops-mail（mail-relay 経由）を有効にした。」と amtool の SUCCESS
#   WARNING が出ていたら to が空である（§1 をやり直す）
kubectl -n platform-infra exec deploy/alertmanager -c alertmanager -- \
  amtool --alertmanager.url=http://localhost:9093 config routes
#   期待値: 既定の route の受信先が ops-mail（default-null ではない）
```

## 3. 試験警報を 1 本流す

```sh
kubectl -n platform-infra exec deploy/alertmanager -c alertmanager -- \
  amtool --alertmanager.url=http://localhost:9093 alert add AlertmanagerMailTest \
  severity=warning job=manual-test --annotation=summary="試験警報（Runbook §3）"
```

API で流す場合（同じ内容）:

```sh
kubectl -n platform-infra exec deploy/alertmanager -c alertmanager -- \
  wget -qO- --header='Content-Type: application/json' \
  --post-data='[{"labels":{"alertname":"AlertmanagerMailTest","severity":"warning","job":"manual-test"},"annotations":{"summary":"試験警報"}}]' \
  http://localhost:9093/api/v2/alerts
```

確かめること（`group_wait` は 30 秒）:

1. Alertmanager の通知が成功している:
   `kubectl -n platform-infra exec deploy/alertmanager -c alertmanager -- wget -qO- http://localhost:9093/metrics | grep 'alertmanager_notifications_total{integration="email"}'`
   が 1 以上、`alertmanager_notifications_failed_total{integration="email"}` が 0。
2. 近接 MTA が受け取って上流へ渡した:
   `kubectl -n platform-infra logs deploy/mail-relay -c postfix --since=5m | grep -E 'from=<noreply@platform.localhost>|status=sent'`
3. 宛先に件名 `[FIRING:1] AlertmanagerMailTest manual-test (warning)` が届いた（上流が捕捉用 MTA なら Mailpit の画面）。
   解消（`[RESOLVED]`）は試験警報の期限（既定 5 分の resolve_timeout 後）に届く。
4. **届いた日時と宛先の種別（個人の箱か共有の箱か）を記録する。** 運用仕様書の暫定経路を閉じる条件 2 の証跡になる。

## 4. 失敗したとき

| 症状 | 見るところ | 原因の例 |
| --- | --- | --- |
| Pod が `Init:Error` | `kubectl -n platform-infra logs deploy/alertmanager -c render-config` | `to` の形が不正（空白以外の余計な文字・引用符） |
| `alertmanager_notifications_failed_total{integration="email"}` が増える | `kubectl -n platform-infra logs deploy/alertmanager -c alertmanager \| grep -i email` | relay へ届かない（NetworkPolicy `mail-relay-ingress-alertmanager` が無い・relay が NotReady） |
| relay のログに `reject` | `kubectl -n platform-infra logs deploy/mail-relay -c postfix` | 差出人が `platform.localhost` 以外（設定を手で変えた） |
| relay は受けたが届かない | relay のログの `status=deferred` / キュー長の警報 | 上流の停止・資格情報。手順は [`keycloak-smtp-relay-setup-runbook.md`](keycloak-smtp-relay-setup-runbook.md) |

## 限界

- 近接 MTA 自身が止まると、その警報はメールでは届かない（同じ relay を通るため）。Alertmanager と Grafana の画面には出る。
- 上流が 30 分を超えて止まると、その間のメールは relay のキュー寿命で捨てられる。同じ警報は 4 時間ごとに再送される。
- Grafana の暫定の警報は宛先を持たないまま残す。試験警報の到達を記録した後に別の変更で削除する（併存させない）。
