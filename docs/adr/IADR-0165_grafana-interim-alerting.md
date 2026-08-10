---
title: IADR-0165 SLO の暫定通知先は Grafana 統合アラートへ「検知まで」を配線し、宛先は書かない
type: impl-adr
status: Accepted
related_ids:
  - NFR-21
  - ADR-0006
  - IADR-0130
  - IADR-0164
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/05_observability-ops.md"
---

# IADR-0165: SLO の暫定通知先を Grafana 統合アラートへどう配線するか（#665）

- 状態: Accepted
- 日付: 2026-08-10
- 決定者: claude（実装）

## 起点・関連

- **NFR-21**（MTTR 30 分以内・**障害検出 5 分以内**）。計画 ADR: **ADR-0006**（アラートは Alertmanager を用いる）
- 計画の裁定: planning#286 → planning `b8002cc` **決定 42**
  「SLO の一次検知 = Grafana の内蔵アラートを暫定の通知先とする。欠けているのは検知そのものではなく通知の送り先だけである」
- 実装 issue: **#665**（出所は #546 / [IADR-0164](./IADR-0164_llm-cost-monthly-review-interim-control.md) 決定 6）
- 作業仕様書: [20260810_issue-665](../specs/20260810_issue-665_grafana-alerting.md)

## 文脈

`deploy/prometheus/alerts.yml` の 5 ルールは Prometheus が実際に評価しているが、
`prometheus.yml` の `alertmanagers.targets` が**空**（compose・k8s の 2 か所とも。実測）であり、
**発火しても誰にも届かない**。計画は「Alertmanager が配備されるまでの暫定として Grafana の
内蔵アラートを使う」と裁定した。**ADR-0006 は改めない。**

## 決定 1: **配線の前に検証手段を決める。この環境では「Grafana が受理するか」は確かめられない**

#665 は「Grafana を起動できない環境で『配線した』と記録してはならない」と明示している。**実測して決めた。**

| 案 | 可否（**実測**） | 判断 |
| --- | --- | --- |
| **c**: `docker compose up grafana` して `/api/v1/provisioning/alert-rules` が 5 件返すのを見る | **不可**。`docker` CLI はあるが daemon へ到達できない（`dial unix /var/run/docker.sock: no such file or directory`） | **採れない** |
| **b**: `k8s-local-up-smoke` 相当へ足す | **不可**。手元に `kubectl` もクラスタも無い。**CI の当該ジョブも「stub-on-PATH, no cluster」**であり（`ci.yml:312`）、実クラスタへ apply しない —— **そこへ足しても Grafana は起動しない** | **採れない** |
| **a**: provisioning YAML の内部整合をスキーマ検査する | **可** | **採用** |

**採用は a のみである。** したがって本 PR が主張できるのは
**「provisioning ファイルが `alerts.yml` と整合している」までで、「Grafana が受理する」ではない。**
この限界を**運用仕様書とファイル冒頭の両方に書く**。

> **#546 の先例に倣う。** ダッシュボード `llm-usage.json` も「式が Grafana で意図どおり描画されるかは未検証」と
> 明記して着地した。**同じ作法を、より強い言い方で使う** ——
> **ダッシュボードは見えないだけだが、アラートは鳴らなくても気づけない。**

## 決定 2: **datasource へ固定 uid を宣言する**（アラートの前提。#665 に無い発見）

Grafana のアラートルールは datasource を **`uid`** で指すが、
`deploy/grafana/provisioning/datasources/datasources.yaml` の 3 つ（Prometheus / Loki / Tempo）は
**いずれも `uid` を宣言していなかった**。宣言が無い場合 Grafana は provisioning 時に uid を生成するため、
**アラートから `datasourceUid: prometheus` と書いても解決しない。**

**さらに、既存の Tempo 設定がその uid を参照していた** —— **つまり Tempo の連携は
現状すでに切れている疑いがある。** 本決定は副次的にこれも解消する。

**★ ただし「どちらの経路で解消するか」は同じではない。数え直した。**

| 参照 | compose（`datasources.yaml`） | k8s（`grafana.yaml` の inline） | uid 宣言で解消するか |
| --- | --- | --- | --- |
| `serviceMap.datasourceUid: prometheus` | **有り** | **有り** | **両経路で解消する** |
| `tracesToLogs.datasourceUid: loki` | **有り** | **無し** | **compose のみ**。k8s は**ブロックごと存在しない** |
| `search.hide` | 有り | **無し** | uid とは無関係 |

**k8s 側には `tracesToLogs` ブロックそのものが無い**ため、**traces-to-logs は k8s 経路では
uid を宣言しても繋がらない。**「両方直った」と書くと誤りになるので、**表で分けて書く。**

**この datasource の乖離は本 PR が持ち込んだものではない**（本 PR の差分は `uid:` 3 行の追加のみ）。
**#665 の射程はアラートであり datasource の同内容化ではない**ため、**本 PR では直さず、
フォローアップとして残す**（下記 §結果 フォローアップ 2）。**検査器も datasource は突合しない**
——突合するのは `alerting/` の compose ↔ k8s だけである。

**ダッシュボードは触らない。** `"datasource": "Prometheus"` と**名前**で参照しており、
**uid 宣言は名前参照を壊さない。**

## 決定 3: **`contactPoints` / `policies` を書かない**（意図的な不作為）

Grafana 統合アラートの provisioning は `groups` / `contactPoints` / `policies` の 3 つを取れる。
**本 PR は `groups` だけを書く。**

**理由**: 宛先（メール / Slack 等）は**実環境の判断**であり、Alertmanager の受信先設定と同じ性質のものである。
**この環境では実在する宛先を知らない。** 知らないまま `contactPoints` を書けば、
**ファイルの見た目は「通知先まで配線済み」になるが、実際には誰にも届かない** ——
**#546 が問題にした「統制を定めた」と「統制が働いている」の混同**を、こちらから作ることになる。

**代わりに限界を明示する。** 暫定期間に人が気づく経路は**「Grafana の Alerting 画面を見る」ことだけ**であり、
**NFR-21「障害検出 5 分以内」を満たしているのは評価の側だけである。人が気づくまでの時間は見に行く間隔に等しい。**
この 1 文を **`slo-alerts.yaml` の冒頭と運用仕様書の両方**へ置く。

> **決定 3 は「やらない」という決定である。** 何もしなかったのではなく、
> **書ける場所に意図的に書かなかった**ため、ADR に残さないと後から「書き忘れ」と読まれる。

## 決定 4: **compose と k8s の 2 か所へ入れ、乖離を機械で止める**

k8s（`deploy/local/observability/grafana.yaml`）は ConfigMap へ inline する既存方針（datasources と同じ形）に揃える。
**片方だけだと経路 B が無音のまま**である。compose 側は `docker-compose.yml` が
`./grafana/provisioning` を丸ごとマウントしているため**変更不要**（実測）。

**二重管理になるため、`scripts/check-grafana-alerting.js` が同内容であることを検査する。**

## 決定 5: **暫定経路を閉じる条件を先に書く**（併存させない）

**暫定は放置されると恒久になる。** 閉じる条件を運用仕様書へ明記した（3 条件。要は
「Alertmanager 経由で通知が実際に届いたことを 1 件以上確かめた」）。

**併存させない理由**: 同じ 5 ルールが 2 系統で評価されると**同じ事象に対して 2 通の通知が出る**。
重複は「片方は既知の誤報だ」という運用習慣を生み、**本物の通知を握り潰す方向に働く。**

**消し忘れが CI で表面化するようにした**: 検査器は対象 4 ファイルのいずれかが読めないと fail する
（[IADR-0130](./IADR-0130_test-spec-coverage-ratchet.md) の 0 件走査の門）。
**`alerting/` だけ消して検査器を残すと CI が落ちる** ため、**両方を同時に消すことになる。**

## 検査器（採れた案 a の中身）

`scripts/check-grafana-alerting.js` が次の 5 点を見る。

1. ルール数が `deploy/prometheus/alerts.yml` と一致する
2. ルール名（`alert:` ↔ `title:`）が 1 対 1 に対応する
3. 参照する `datasourceUid` が datasources に実在する（`__expr__` 等の組込みは除く）
4. compose の YAML と k8s の inline が同内容である
5. 必須キー（`apiVersion` / `groups` / `condition` / `data` / `noDataState` / `execErrState`）が揃っている

**見ていないもの**: **Grafana が受理するか**（決定 1）。**式が正しい結果を返すか**（Prometheus 側と同一式である、までしか言えない）。

### 門は 2 つあり、**別々に**変異試験する

`scripts/scripts.repo.test.js` へ自動回帰として常設した。

| 門 | 発火条件 |
| --- | --- |
| **A** | 対象 4 ファイルのいずれかが読めない |
| **B** | ファイルは読めるが**ルールを 1 件も拾えない**（正規表現が実書式に合っていない型） |

> **★ 1 つの変異で両方を確かめたつもりにならない。** 最初は門 A の試験（空リポジトリ）だけを書いており、
> **門 B を消しても緑のままだった** —— メタ変異試験で実測して気づいた。門 B は**ファイルが揃っている**
> 状態でしか通らないため、専用のフィクスチャが要る。
> さらに**そのフィクスチャがルール 0 件以外の違反を踏むと、門 B を消しても「別の理由で exit 1」になり
> 試験が空振りする**（`groups: []` が必須キー検査に掛かっていた）。**フィクスチャは健全側へ寄せた。**

**変異試験は実データにも当てる。** フィクスチャだけだと「実ファイルの書式が正規表現に合っていない」型の
空振りを捕まえられない（#664 の枝番行 `| 6-b |`、本 issue の `^  - alert:` 決め打ちがまさにこれ）。
実ファイルから 1 ルールだけ改名して違反が出ることを確かめている。

## 結果

- `deploy/grafana/provisioning/alerting/slo-alerts.yaml`（新規。5 ルール。**宛先は書かない**）
- `deploy/grafana/provisioning/datasources/datasources.yaml`（`uid` を 3 つ宣言）
- `deploy/local/observability/grafana.yaml`（ConfigMap inline ＋ mount）
- `scripts/check-grafana-alerting.js`（新規。自己試験 10 件）／`scripts/scripts.repo.test.js`（7 件追加）
- `docs/operations/operations.md`（限界・閉じる条件）

### フォローアップ

1. **配備時に `/api/v1/provisioning/alert-rules` が 5 件返すことを確かめる**（決定 1 の未検証部分）。
2. **Tempo の連携を配備時に確かめ、k8s 側の datasource 欠落を埋める**。決定 2 の表のとおり
   **サービスマップは両経路、traces-to-logs は compose のみ**が uid 宣言で解消する見込みであり、
   **k8s には `tracesToLogs` / `search.hide` がそもそも無い**。**この乖離は #665 の射程外**なので、
   **別 issue として起票した（#674）**（datasource の compose ↔ k8s 同内容化。検査器の対象を
   `alerting/` から datasources へ広げるかも、そこで判断する）。
   なお**宣言を足しただけで連携が復活したことは未検証**である（決定 1）。
3. **Alertmanager 配備時に暫定経路を削除する**（決定 5 の 3 条件。#546 で追跡）。
