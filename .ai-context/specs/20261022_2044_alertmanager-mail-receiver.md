---
title: Alertmanager から近接 MTA（mail-relay）経由でメールを送る受信先を 1 本置き、既定の route をそこへ向ける（宛先は Secret 注入・空なら警告して無効。#2044）
type: spec
status: done
related_ids: [NFR-21, ADR-0006, ADR-0045, ADR-0078, IADR-0571, IADR-0304, IADR-0165, IADR-0404]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md（§アラートの通知経路）
  - planning:projects/microservices-platform/07_adr/ADR-0006（アラートは Alertmanager を用いる）
  - planning:projects/microservices-platform/07_adr/ADR-0045（メール送信経路）
  - planning:projects/microservices-platform/07_adr/ADR-0078（近接 MTA）
issue: "#2044"
---

# 仕様書: 警報の宛先を 1 本置く（#2044）

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。実装の基点は MSP `origin/develop` `147e66fb`。判断は IADR-0571。

## 起点となる計画書（トレーサビリティ）

- 非機能: **NFR-21**（障害検出 5 分以内・MTTR 30 分以内）
- 計画 ADR: **ADR-0006**（アラートは Alertmanager）／**ADR-0045**（メールを用いる場合はこれに従う。計画 `05_observability-ops` §アラートの通知経路の末尾）／**ADR-0078** 決定 2（送出は近接 MTA 経由）
- 先行 IADR: **IADR-0304** 決定 2（既定 `default-null`）・**IADR-0165**（Grafana の暫定経路）・**IADR-0404** 決定 3（クラスタ内の 1 ホップは平文を受容）

## 問題

警報 26 ルールの発火は Alertmanager まで届くが、受信先が `default-null` しか無い（compose・経路B とも）。
Grafana 暫定経路も contact point を持たない。**人が気づくまでの時間は画面を見に行く間隔に等しい。**
PoC には近接 MTA（`mail-relay`）が配備済みである。

## 受け入れ基準

1. 経路B（`deploy/local/observability/alertmanager.yaml`）に、**mail-relay 経由でメールを送る受信先**が 1 本あり、
   **既定の route がそこを向く**（`default-null` ではない）。
2. smarthost は **`deploy/mail-relay/mail-relay.yaml` の Service 名とポート（`mail-relay:587`）から導かれる**値に一致する。
3. 宛先アドレスは**リポジトリに実値を持たない**。Secret `alertmanager-mail` のキー `to` を env で受け、
   起動時に設定へ描画する。Secret の供給経路として OpenBao → ESO の ExternalSecret（新ファイル）を置く。
4. **宛先が空（Secret が無い・キーが空）なら警告を出して `default-null` へ落とす**（Alertmanager は起動する）。
   **不正な値なら描画を失敗させる**（起動しない）。どちらを採るかとその理由は IADR-0571 に書く。
5. 差出人は近接 MTA の差出人の門（`ALLOWED_SENDER_DOMAINS=platform.localhost`）を通る `noreply@platform.localhost`。
6. Alertmanager から mail-relay:587 への ingress を NetworkPolicy の**別宣言**で開く（既存宣言は書き換えない）。
7. 試験（`scripts/scripts.repo.test.js`）が 1〜6 を固定し、描画スクリプトを実際に走らせて
   空・正常・不正（YAML 注入）の 3 通りを確かめる。変異（route を null へ戻す等）で赤になる。
8. Grafana の contact point は**揃えない**（理由は IADR-0571 決定 5）。暫定経路の削除は後続 issue。

## 範囲外

- 警報の重さごとの経路（critical だけ即時など）。issue は「1 本」を求めている。
- compose 経路のメール受信先（compose に近接 MTA が無い。`default-null` のまま）。
- stg/prod の Helm（Alertmanager リソースが無い）。
- Grafana 暫定経路の削除（テスト通知の到達を PoC で記録した後。後続 issue）。

## 方式

- ConfigMap `alertmanager-config` に 3 キーを持つ: `alertmanager.yml`（従来の null 設定＝無効時の落ち先）、
  `alertmanager-mail.yml.tmpl`（受信先 `ops-mail`。宛先はプレースホルダ）、`render.sh`（描画）。
- initContainer（同じ alertmanager イメージ。busybox の sh/grep/sed と amtool を持つ）が `render.sh` を走らせ、
  emptyDir へ `alertmanager.yml` を書く。本体はそれを読む。描画後に `amtool check-config` を通す。
- 宛先の文字種を検証してから sed で差し込む（YAML・sed への注入を止める）。カンマ区切りの複数宛先を許す。
- Deployment に Reloader の注釈（`secret.reloader.stakater.com/reload: alertmanager-mail`）。

## 母集合（規則 9・10。誤りの側の文字列で走査した）

走査語: `default-null` / `画面まで` / `宛先は依然` / `その先の宛先は未配線` / `push 配信の宛先`（`git grep`、2026-10-22）。

| 箇所 | 扱い |
| --- | --- |
| `deploy/alertmanager/alertmanager.yml` 冒頭 | 直す（経路B は宛先を注入できる・compose は null のまま、と書き分ける） |
| `deploy/local/observability/alertmanager.yaml` | 直す（本体） |
| `deploy/prometheus.yml` の alerting 注記 | 直す（同上の書き分け） |
| `docs/operations/operations.md` ★通知先の現状・閉じる条件 2・未決事項 | 直す（器は置いた／到達の記録は PoC 待ち） |
| `docs/operations/alertmanager-mail-receiver-runbook.md` | 新設（配備・確認・試験警報の手順） |
| `.ai-context/adr/IADR-0304` 決定 2 | 追記ブロックで後継（IADR-0571）を指す（本文は書き換えない） |
| `deploy/grafana/provisioning/alerting/slo-alerts.yaml` 冒頭 | 直さない。Grafana 側は依然 contact point を持たず記述は真 |
| `IADR-0466` / `IADR-0481` / `IADR-0432` / `IADR-0322` / `IADR-0345` の言及 | 直さない。凍結記録（当時の事実） |
| `docs/operations/keycloak-smtp-relay-setup-runbook.md:391`・`docs/tests/SC-15_password-reset.md:132`・`llm-*-runbook.md` | 直さない。宛先が未供給の既定では依然真であり、現状の正は運用仕様書の★に寄せる（そこを直す） |

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- 文書系: `node scripts/check-trace-blocks.js` / `node scripts/gen-knowledge-graph.js --check` / `node scripts/check-doc-links.js` ほか CI と同じもの
- 変異: route を `default-null` へ戻す・smarthost のポートを変える・検証の正規表現を外す → 試験が赤

## 検証の結果（2026-10-22）

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → **1134 件すべて通過**（#2044 節 9 件を含む。IADR-0571 へ改番し、#2048〔IADR-0570〕を含む develop `af01abc5` を merge した後に再実走）。`node scripts/k8s-local-up.test.js` → 330 件通過（同じ merge 後）。
- 変異: 既定 route を `default-null`・smarthost の port を 25・NetworkPolicy の名前を変える・`optional: true` を外す → いずれも赤（各 1 件）。
  描画の形の検査を外す変異は試験の中に置いた。
- 手元で Alertmanager v0.27.0（公式リリースのバイナリ）と SMTP の受け口を立て、描画した設定（smarthost だけ 127.0.0.1 へ差し替え）で
  `amtool alert add AlertmanagerMailTest severity=warning job=manual-test` を流した → `MAIL FROM:<noreply@platform.localhost>`・
  `RCPT TO:<ops@example.com>`・件名 `[FIRING:1] AlertmanagerMailTest manual-test (warning)` を受信。`amtool check-config` は宛先あり・なしとも SUCCESS。
- **稼働クラスタ（PoC）では未確認。** 試験警報の到達の記録は Runbook §3 に沿って PoC で行う。
- `check-deploy-manifests.js` は手元に helm / kubectl / kubeconform が無く走らない（CI で確かめる）。
