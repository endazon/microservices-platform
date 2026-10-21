---
title: IADR-0571 Alertmanager の受信先を近接 MTA 経由のメール 1 本とし、宛先は Secret から起動時に描画する（空なら警告して default-null・不正なら起動しない。Grafana の contact point は揃えない）
type: impl-adr
status: Accepted
related_ids: [NFR-21, ADR-0006, ADR-0045, ADR-0078, IADR-0304, IADR-0165, IADR-0404, IADR-0421]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md（§アラートの通知経路）
  - planning:projects/microservices-platform/07_adr/ADR-0006（アラートは Alertmanager を用いる）
  - planning:projects/microservices-platform/07_adr/ADR-0045（メール送信経路）
  - planning:projects/microservices-platform/07_adr/ADR-0078（近接 MTA）
related_specs:
  - ../specs/20261022_2044_alertmanager-mail-receiver.md
---

# IADR-0571: 警報の宛先を近接 MTA 経由のメール 1 本にする（#2044）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: claude（#2044。PoC セッションの監査指摘）

## 起点・関連

- 起点 issue: #2044（Refs #546）。
- 関連する計画書 ID: NFR-21。計画 ADR: ADR-0006（アラートは Alertmanager）・ADR-0045（メールを宛先にするならこれに従う）・
  ADR-0078 決定 2（送出は近接 MTA 経由）。
- 先行: [IADR-0304](./IADR-0304_alertmanager-deployment-and-null-receiver.md) 決定 2（既定 `default-null`。本 ADR が経路B について後継する）・
  [IADR-0165](./IADR-0165_grafana-interim-alerting.md)（Grafana の暫定経路）・
  [IADR-0404](./IADR-0404_nearby-mta-relay-and-realm-ownership.md) 決定 3（クラスタ内の 1 ホップは平文を受容）。
- 作業仕様書: [20261022_2044_alertmanager-mail-receiver](../specs/20261022_2044_alertmanager-mail-receiver.md)。

## コンテキストと課題

警報 26 ルールの発火は Alertmanager まで届くが、受信先は `default-null` しか無かった。IADR-0304 決定 2 が
「実環境の宛先は利用者が決める事柄で、実装側で代替値を置けない」として受信先を書かなかったためである。
PoC には近接 MTA（`deploy/mail-relay/`・Service `mail-relay:587`）が配備済みで、上流（Secret `keycloak-smtp`）も供給されている。
**欠けているのは「宛先のアドレスを差し込む口」だけ**であり、アドレスそのものはリポジトリに置かなくてよい。

## 決定

### 決定 1 — 経路B の Alertmanager に、近接 MTA へ投函するメールの受信先 `ops-mail` を 1 本置き、既定の route をそこへ向ける

- smarthost は `mail-relay.platform-infra.svc.cluster.local:587`（`deploy/mail-relay/mail-relay.yaml` の Service が単一情報源。試験が導出して突き合わせる）。
- 差出人は `noreply@platform.localhost`。近接 MTA の差出人の門（`ALLOWED_SENDER_DOMAINS=platform.localhost`）を通り、
  上流が実在の送信元を要るときは relay の `smtp_generic_maps` が Secret `keycloak-smtp` の `from` へ写す（Keycloak のメールと同じ道）。
- `require_tls: false`。Alertmanager → relay はクラスタ内の平文の 1 ホップであり、Keycloak → relay と同じ受容（IADR-0404 決定 3）。
  relay → 上流の STARTTLS は relay が強制する（ADR-0045 決定 5）。Alertmanager は `require_tls: false` のとき STARTTLS を試みない。
- `send_resolved: true`（解消も届ける。解消が届かないと「まだ鳴っているか」を見に行く必要が残る）。
- route の束ね（`group_by` / `group_wait` / `group_interval` / `repeat_interval`）と `inhibit_rules` は従来のままにする。
  **重さごとの経路（critical だけ即時など）は置かない** —— #2044 が求めるのは 1 本であり、分ける根拠となる運用実績が無い。
- Alertmanager → mail-relay:587 の ingress は NetworkPolicy の別宣言（`mail-relay-ingress-alertmanager`）で開く。
  可観測性オーバーレイ（opt-in）に置き、Alertmanager の居ないクラスタに穴を開けない。

### 決定 2 — 宛先は Secret `alertmanager-mail` のキー `to` が与え、initContainer が起動時に設定へ描画する

- Alertmanager の設定は環境変数を展開しない（v0.27 の `email_configs.to` にはファイル参照も無い）。
  そこで同じイメージの initContainer（busybox の sh / grep / sed と `amtool` を持つ）が ConfigMap のテンプレートへ
  宛先を差し込み、emptyDir へ書いた設定を本体が読む。描画後に `amtool check-config` を通す。
  **イメージは増やさない**（供給網へ 1 つ増やす理由が無い）。
- Secret の供給は OpenBao → ESO（`deploy/local/vault/eso/externalsecret-alertmanager-mail.yaml`。KV `secret/msp/alertmanager-mail` の `to`）。
  ESO を使わない環境は同名・同キーの Secret を直接作ればよい。
- **bootstrap は seed しない。** 宛先には dev の既定値が無く、既定値を置くと「配線したつもり」になる（IADR-0304 決定 2 の理由は経路B でもここでは生きる）。
  起動器は OBSERVABILITY=1 のとき ExternalSecret を apply し（KV が無いあいだは SecretSyncedError のまま＝未供給が状態に見える）、同期は待たない。
- Pod 起動時に 1 度だけ描画するので、値を差し替えたら作り直す（Reloader の注釈を置く。無い環境は `rollout restart`）。

### 決定 3 — 宛先が空なら警告して `default-null` へ落とす。値が不正なら起動しない

| 状態 | 振る舞い | 理由 |
| --- | --- | --- |
| Secret が無い・`to` が空 | **警告を出して `default-null` の設定で起動する** | 可観測性オーバーレイは宛先を持たない開発者・CI も使う。起動を止めると、**いま唯一働いている「Alertmanager の画面までは届く」床まで失う**（Prometheus の送り先も消える）。未供給は ExternalSecret の状態と initContainer のログに出る |
| `to` がメールアドレス（カンマ区切り可）の形でない | **initContainer が exit 1 → Alertmanager は起動しない** | 値を入れたのは「届けたい」という意思の表明であり、黙って `default-null` へ落とすと**届けたつもりで届かない**。Pod が Init:Error になれば `kubectl get pods` で即座に見える。形の検査は YAML・sed への注入も止める |
| 正しい値 | `ops-mail` を描画し、`amtool check-config` を通して起動する | — |

「描画を失敗させる」を空のときにも採らなかったのは上の 1 行目の理由による。**fail-closed にすべきは「意図した宛先が壊れている」場合であって、
「宛先がまだ無い」場合ではない** —— 後者は IADR-0304 が既定として受け入れた状態そのものである。

### 決定 4 — compose 経路は `default-null` のままにする

compose には近接 MTA が無い。メールを足すには relay か上流を compose へ持ち込む必要があり、#2044 の範囲（PoC＝経路B）を越える。
compose の `deploy/alertmanager/alertmanager.yml` と経路B の無効時の設定（ConfigMap の `alertmanager.yml` キー）は同内容に保つ。

### 決定 5 — Grafana の contact point は揃えない。暫定経路は「テスト通知の到達」を PoC で記録してから削除する（後続 issue）

運用仕様書の閉じる条件 2 は「Alertmanager 側に受信先が設定され、**テスト通知が実際に届いた**」である。
**本 ADR が満たすのは前半だけ**で、後半は PoC で試験警報を流して記録するまで満たされない。
Grafana に同じ宛先を配線すると、閉じる条件が満たされた瞬間に**同じ事象が 2 通届く**（暫定経路を併存させない理由そのもの）。
したがって Grafana 側は宛先を持たないまま残し、到達を記録した変更で `deploy/grafana/provisioning/alerting/` と
`scripts/check-grafana-alerting.js` を削除する（後続 issue に分ける。削除は検査器・文書の追随を伴い範囲が大きい）。

## 結果

- **良い影響**: PoC で宛先の Secret を 1 つ入れるだけで、26 ルールの発火がメールで人に届く。NFR-21「検出 5 分以内」の
  「人が気づく」側が、画面を見に行く間隔から `group_wait`（30 秒）＋配送時間へ縮む。
- **悪い影響 / 受容するリスク**:
  - **近接 MTA 自身の障害は、メールでは届かない**（`MailRelayQueue*` は同じ relay を通る）。画面と Grafana には出る。
    第 2 の経路（チャット）を足すまでの受容である。
  - relay のキュー寿命は 30 分（リセットリンクに合わせた値）。上流が 30 分を超えて止まると、その間の警報メールは捨てられる。
    `repeat_interval`（4 時間）で同じ警報は再送される。
  - 個人アカウントの上流（ADR-0045 決定 1-b）では送信上限（500 通/日）を Keycloak のメールと分け合う。
    束ね（`group_interval` 5 分・`repeat_interval` 4 時間）の下で 26 ルールが同時に鳴り続けても上限には遠いが、実測はしていない。
  - 本文は Alertmanager の既定テンプレート（ラベル・注釈・発火元への URL）。注釈は運用上の説明だけで、資料の本文・検索語を含まない（ADR-0045 決定 7 の範囲内）。
- **フォローアップ**:
  1. PoC で試験警報の到達を記録する（手順は `docs/operations/alertmanager-mail-receiver-runbook.md`）。
  2. 到達を記録した後、Grafana 暫定経路を削除する（後続 issue）。
  3. stg/prod の Helm へ Alertmanager を展開するときは、同じ「Secret から描画」の形を採る（Helm 側には Alertmanager リソースがまだ無い）。

## 変異で確かめたこと（作業仕様書 §検証）

試験（`scripts/scripts.repo.test.js` の #2044 節）は、route を `default-null` へ戻す・smarthost のポートを変える・
宛先の形の検査を外す・NetworkPolicy を消す、の各変異で赤になる。描画スクリプトは試験の中で実際に走らせ、
空・正常・不正（YAML 注入）の 3 通りを確かめる。加えて手元で Alertmanager v0.27.0 と SMTP の受け口を立て、
`amtool alert add` の試験警報が `MAIL FROM:<noreply@platform.localhost>` / `RCPT TO:<宛先>` で届くことを確かめた（稼働クラスタでは未確認）。
