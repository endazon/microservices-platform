---
title: 作業仕様書 — 取引ユニットをクラウドの単一ノードへ移すための MSP の最小構成（deploy/cloud）を作る（#2401）
type: spec
status: done
related_ids: [NFR-18, NFR, ADR-0008, ADR-0124, IADR-0574, IADR-0591, IADR-0605, IADR-0672, IADR-0674]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#2401"
---

# 作業仕様書 — deploy/cloud の最小構成（#2401）

> 本仕様書は実装着手前に作成した（着手 2026-11-05。develop `b05b19b9`）。判断の記録は **IADR-0674**（［2026-11-05 追記 / #2401］番号は 0674 で確定した。develop の 0673 を取り込んだ時点の最大＋1）。
> 🔴 クラスタには触らない。確かめは描画（kubectl kustomize・helm template）とスタブの下の実行だけで行う。値は表示しない・リポジトリに置かない。

## 起点（issue #2401 の本文を正とする）

- 取引ユニット（AST）の PoC を、自宅の PC からクラウドの単一ノード（k3s・8 GB）へ移す。AST が MSP から要るのは Postgres・RabbitMQ・Keycloak の `platform` レルム・LLM ゲートウェイだけ。
- 受け入れ基準（試験で固定）:
  1. 描画に valkey・qdrant・mailpit・knowledge・BFF・frontend の資源が 1 つも無い。
  2. 描画に LoadBalancer の Service と hostPort が無い。
  3. 描画にリポジトリの既定の資格情報の値が無い。
  4. Keycloak の起動の引数に start-dev が無い。
  5. bootstrap の手順に、ファイアウォール・k3s のフラグ・データの暗号化・時刻同期・更新の時間帯が書かれている。
  6. ローカルの PoC（deploy/local）の描画は変わらない。

## 実測で確かめたこと（規則 10。issue の数えを転記しない）

- 取引ユニットの DB は **7 つ**（`src/ai-stock-trading/infra/postgres/init/01-create-databases.sql` と AST の chart の `database` が一致）。issue の「9 DB」とは違う。7 つで作る。
- 取引ユニットのブローカのユーザーは `deploy/local/rabbitmq-setup/users.txt` の名前空間が `ai-stock-trading` の行（11 件）。MSP の LLM ゲートウェイはブローカを使わない（users.txt に行が無い。chart の `messaging` も無い）。
- 取引ユニットの機密クライアントは realm の宣言の 5 つ（`ai-stock-trading-svc`・`-kb-writer`・`-kb-reader`・`-llm-caller`・`-owner`）。LLM ゲートウェイはクライアントを持たない（トークンを検証するだけ）。
- realm の宣言は、人の利用者 10 人（`admin`・`developer`・`e2e-*` ほか）と、試験の置換（`${E2E_*}`）・SMTP の送出先を持つ。クラウドへは持ち込まない。
- ローカルの宣言の字面の既定値: Postgres の初期化の `WITH PASSWORD '…'`（MSP の利用者・取引ユニットの利用者）、Keycloak の H2 の資格情報。クラウドでは写さない。
- 計画の NFR（秘匿は Vault）との違いは、既存の planning#868 の 3 で計画側へ挙がっている（同件を起票しない）。

## 方針（IADR-0674）

1. `deploy/cloud/infra`（kustomize。resources だけで変換を持たない＝宣言がそのまま描画）: 名前空間 2 つ・Postgres（PVC・TLS・初期化は Secret の env を `\getenv` で読む）・RabbitMQ（ローカルの宣言を写す・到達は取引ユニットだけ）・Keycloak（`start`・DB は Postgres・hostname はクラスタの中の名前・キャッシュは local）・collector（受けて捨てる）・MSP の名前空間の基盤の別名・NetworkPolicy。
2. `deploy/cloud/values-cloud.yaml`: LLM ゲートウェイだけを on。chart の NetworkPolicy は描かない（入り・出とも `deploy/cloud/infra/networkpolicy.yaml`。［2026-11-05 追記 / #2401］レビューの 🔴：chart の `fromAst` は `networkPolicy.enabled` の内側で、`enabled=false` では描かれなかった）。メッシュ・エッジ・HPA/PDB・同期の後の確かめは off。
3. 資格情報: `deploy/cloud/bootstrap-secrets.sh --live` が**初回に乱数で作る**（在れば触らない）。利用者名も乱数の接尾辞。realm は `realm-for-cloud.js` が宣言から組む。証明書はその場の CA で発行し、CA の鍵は保存しない。秘密の値はリポジトリに置かない（暗号化したファイルも置かない）。
4. 秘匿管理（OpenBao / ESO）は持ち込まない（IADR-0674 決定 4）。Secret は k3s の保存時の暗号化（`--secrets-encryption`）とデータの領域の LUKS で守る。
5. 手順書 `deploy/cloud/README.md`: ファイアウォール（SSH だけ）・SSH の鍵認証・LUKS・chrony・更新の時間帯（JST 07:00〜21:00。［2026-11-05 追記 / #2401］自動の再起動はしない）・k3s のフラグ（traefik・servicelb を外す・`--secrets-encryption`・API サーバーはループバック）・SSH のトンネル・イメージの取り込みと digest の記録・配備の順序。

## 受け入れ基準と試験の写像

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | 持ち込まない資源が描画に無い（基盤・chart） | `scripts/deploy-cloud.test.js`（CI の static-checks。kubectl・helm）・`scripts.repo.test.js` の IADR-0674 節（宣言そのもの） |
| AC2 | LoadBalancer / NodePort・hostPort・hostNetwork が無い | 同 |
| AC3 | Secret を描かない・資格情報の env は Secret から読む・既知の開発用の字面と SQL の字面のパスワードが無い | 同 |
| AC4 | Keycloak は start（start-dev が無い） | 同 |
| AC5 | 外部のイメージは deploy/local/infra と同じ digest | 同 |
| AC6 | chart の描画は LLM ゲートウェイだけで NetworkPolicy を描かない・既定の values では判定に落ちる（陰性対照）。MSP の名前空間の入りは既定で拒否し、LLM ゲートウェイへは取引ユニットの呼び出し元（名前空間 かつ Pod）の 8080 だけ（基盤の宣言） | `scripts/deploy-cloud.test.js`・`scripts.repo.test.js`（陰性対照つき） |
| AC7 | realm は 5 つの機密クライアントと組み込みだけ・secret は渡した値・人の利用者と SMTP を持ち込まない・宣言の値なら止める | `scripts.repo.test.js` |
| AC8 | bootstrap は初回に乱数で作り、在るものを触らず、値を引数・出力に出さない。CA の鍵を保存しない。LLM の鍵はファイルからだけ読む | 同 |
| AC9 | 手順書の項目（ファイアウォール・k3s のフラグ・LUKS・chrony・更新の時間帯・digest） | 同 |
| AC10 | deploy/local は deploy/cloud を参照しない（ローカルの描画は変わらない。本 PR は deploy/local のファイルを変えない） | 同 |
| AC11 | Postgres は TCP を hostssl だけで受け、認証は scram-sha-256 だけ（host・hostnossl・trust・md5 が無い）。空のボリュームからの初回起動が通る（実物のイメージ） | `scripts/deploy-cloud.test.js`・`scripts.repo.test.js`（陰性対照つき）・`scripts/deploy-cloud-postgres.test.js`（docker。CI。宣言を弱めると赤になることを確かめた） |
| AC12 | RabbitMQ はデータを PVC に置き、ノード名を固定し、Recreate で替わる（再起動でキュー・持続のメッセージを失わない） | 同 |
| AC13 | LLM ゲートウェイの受け口は TLS だけ（https・証明書は Secret `llmgateway-tls`・probe は HTTPS・gRPC の h2c の受け口が無い）。`tls` と `grpcPort` の併記は描画で止まる。bootstrap が同じ CA で証明書を発行する | `scripts/deploy-cloud.test.js`（陰性対照つき）・`scripts.repo.test.js`（値・bootstrap の証明書の検証） |
| AC14 | LLM ゲートウェイはルートのファイルシステムを読み取り専用にし、書き込みは `/tmp` の emptyDir だけ | 同 |
| AC15 | Keycloak はクラスタの中だけ（基盤の Service は ClusterIP と明示・別名は ExternalName だけ）。描画（基盤・chart）にエッジの資源（Ingress・Gateway・HTTPRoute ほか）が無い | 同（陰性対照つき） |

- 赤の確認: `deploy/cloud` と描画の試験を外した状態で、IADR-0674 節の 10 件のうち 9 件が赤（残る 1 件は参照の無いことを見る守り）。
- ［2026-11-05 追記 / #2401］レビューの 🟡 で RabbitMQ の永続化と pg_hba の hostssl を足した（AC11・AC12）。新しい判定は、直す前の宣言に当てて 6 件が赤になることを確かめた。
  キューの耐久性はアプリの側も確かめた。Wolverine の既定でキューは durable。メッセージの持続は送り手の endpoint が Durable のときだけ（`RabbitMqSender` の IL）。取引ユニットで Durable なのは注文の実行のサービスだけ（IADR-0674 残余 6）。
- ［2026-11-05 追記 / #2401］監査の P-1・P-2 で、LLM ゲートウェイの受け口の TLS（アプリで。メッシュは入れない）と読み取り専用のルートを足した（AC13・AC14。IADR-0674 決定 2a・2b）。chart には opt-in の口（`services.<名>.tls`・`readOnlyRootFilesystem`）を足し、既定の描画は変えない。
  証明書の組が一部だけ在るとき、bootstrap は作り直さずに止める（レビューの 🟢。試験で固定）。
  監査の T-1 で、Keycloak をクラスタの中だけに置くこと（AC15）を描画の試験で固定した。
  再レビューの 🟡 で、k3s が LUKS のマウントの前に起動しないこと（drop-in の `ConditionPathIsMountPoint` と `systemctl disable k3s`）と、自動の再起動をしないこと（`Automatic-Reboot "false"`。IADR-0674 残余 4）を手順書に入れ、手順書の試験で固定した（AC9）。
- 🔴 手元に helm・kubectl が無い（導入しない）ので、`scripts/deploy-cloud.test.js` の描画は CI で確かめる。基盤の判定は、宣言そのものへ同じ関数を当てて手元でも確かめた。

## 対象外（issue の関連）

- 取引ユニットの側（values-cloud・OpenD・移行の手順・Secret の写し）は AST#1525。
- バックアップの保管先と警報の通知先は MSP#2402。
- 自製のイメージを digest で固定する chart の口（いまは不変のタグ。digest は手順書で記録する）。
