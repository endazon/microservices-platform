---
title: 作業仕様書 — SLO アラートの暫定通知先を Grafana 内蔵アラートへ配線する（#665）
type: work-spec
status: fixed
related_ids:
  - NFR-21
  - ADR-0006
  - IADR-0164
  - IADR-0165
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/05_observability-ops.md"
---

# 作業仕様書: SLO アラートの暫定通知先（#665）

## 起点

- **NFR-21**（MTTR 30 分以内・**障害検出 5 分以内**）。計画 ADR: **ADR-0006**。
  実装 ADR: **[IADR-0165](../adr/IADR-0165_grafana-interim-alerting.md)**（本作業）／
  **IADR-0164**（出所。#546 の暫定統制）
- 起点 issue: **#665**（出所 #546 → planning `b8002cc` **決定 42**）

## 母集合（自分で引き直した）

### 軸 1: issue 番号で引く

```console
$ git ls-files -z ':!planning' ':!src/ai-stock-trading' | xargs -0 grep -ln '#665'
docs/adr/IADR-0164_llm-cost-monthly-review-interim-control.md
docs/operations/operations.md
docs/specs/20260810_issue-546_llm-cost-monthly-review.md
```

**3 件。いずれも「#665 へ送った」という #546 側の申し送り**であり、実装指示は無い。

### 軸 2: **issue の「実測」を自分で引き直した** —— 1 つ数え方を誤りかけた

| 確かめたこと | 実測 |
| --- | ---: |
| `alertmanagers.targets` | **空。2 か所**（`deploy/prometheus.yml:16` / `deploy/local/observability/prometheus.yaml:19`）。issue のとおり |
| Prometheus のアラートルール | **5 件**。issue のとおり |
| Grafana provisioning | `datasources/` と `dashboards/` のみ。**`alerting/` は無い**。issue のとおり |

> **★ 数え方でつまずいた。** 最初に `grep -c '^  - alert:'` を使って **0 件**を得た ——
> 実体は **6 スペース字下げ**（`      - alert:`）であり、**正規表現の側で絞っていた**。
> **#664 の枝番行（`| 6-b |`）と同じ誤り**である。**ファイルを開いて数え直した。**

### ★ 軸 3: **issue に無い前提の欠落を見つけた** —— datasource に `uid` が無い

```console
$ grep -n 'uid' deploy/grafana/provisioning/datasources/datasources.yaml
（datasource 自身の uid 宣言は 0 件。出てくるのは Tempo の jsonData 内の参照だけ）
```

**Grafana のアラートルールは datasource を `uid` で指す。** ところが 3 つの datasource
（Prometheus / Loki / Tempo）のいずれにも **`uid` が宣言されていない**。

**さらに、既存の Tempo 設定がその uid を参照している**:

```yaml
    jsonData:
      tracesToLogs:
        datasourceUid: loki        # ← この uid を持つ datasource は宣言されていない
      serviceMap:
        datasourceUid: prometheus  # ← 同上
```

**uid を宣言しない場合、Grafana は provisioning 時に uid を生成する**ため、
**`loki` / `prometheus` という uid は存在しない可能性が高い** ——
**Tempo の traces-to-logs とサービスマップの連携が現状すでに切れている疑いがある。**

> **★ ただし compose と k8s で事情が違う。数え直した。**
>
> | 参照 | compose | k8s の inline | uid 宣言で解消するか |
> | --- | --- | --- | --- |
> | `serviceMap.datasourceUid: prometheus` | 有り | 有り | **両経路** |
> | `tracesToLogs.datasourceUid: loki` | 有り | **無し** | **compose のみ** |
> | `search.hide` | 有り | **無し** | 無関係 |
>
> **k8s には `tracesToLogs` ブロックそのものが無い。**「両方直った」と書くと誤りになる。
> **この datasource の乖離は本 PR が持ち込んだものではなく**（差分は `uid:` 3 行のみ）、
> **#665 の射程外**なので**別 issue として起票した（#674）**（下記 §射程外）。

**ダッシュボード側は無事**である（`"datasource": "Prometheus"` と**名前**で参照している）。

> **これは #665 の前提**である。**uid を宣言しないとアラートルールが datasource を指せない。**

## ★ 判断 0: 検証手段を先に決める（#665 の難所）

**#665 は「Grafana を起動できない環境で『配線した』と記録してはならない」と明示している。**
**実測して決めた。**

| 案 | 可否（**実測**） | 判断 |
| --- | --- | --- |
| **c**: `docker compose up grafana` して `/api/v1/provisioning/alert-rules` が 5 件返すのを見る | **不可**。`docker` CLI はあるが**daemon へ到達できない**（`dial unix /var/run/docker.sock: no such file or directory`） | **採れない** |
| **b**: `k8s-local-up-smoke` 相当へ足す | **不可**。手元に `kubectl` もクラスタも無い。**CI の当該ジョブも「stub-on-PATH, no cluster」**（`ci.yml:312`）で実クラスタへ apply しない | **採れない** |
| **a**: provisioning YAML のスキーマ検査 | **可** | **採用** |

**採用は a のみである。したがって「Grafana が受理するか」は本 PR では確かめられない。**
**この限界を IADR と運用仕様書へ明記する**（#546 が問題にした「統制を定めたが働いていない」を作らないため）。

> **先例に倣う。** #546 の Grafana ダッシュボード（`llm-usage.json`）も
> 「**式が Grafana で意図どおり描画されるかは未検証。突合したのは名前の一致まで**」と明記して着地した。
> **同じ作法を、より強い言い方で使う** —— **ダッシュボードは見えないだけだが、アラートは鳴らなくても気づけない。**

## 判断

### 判断 1: **datasource へ `uid` を宣言する**（アラートの前提）

`prometheus` / `loki` / `tempo` の 3 つへ**固定 uid** を宣言する。
**副次的に、Tempo の `datasourceUid` 参照が解決するようになる**（軸 3 の疑いの解消）。
**ただし解消の範囲は経路で異なる** —— 軸 3 の表のとおり、**サービスマップは両経路、
traces-to-logs は compose のみ**である。

**ダッシュボードは触らない** —— 名前参照のままで動く。**uid 宣言は名前参照を壊さない。**

### 判断 2: **アラートは `deploy/grafana/provisioning/alerting/` へ置く。ただし `groups` だけを書く**

Grafana 統合アラートの provisioning は
**`groups`（ルール）・`contactPoints`（通知先）・`policies`（経路）**の 3 つを取れる。
**5 ルールは Prometheus の `alerts.yml` と 1 対 1 に対応させる**（同じ式・同じ `for`・同じ severity）。

> **★ `contactPoints` / `policies` は意図的に書かない**（[IADR-0165](../adr/IADR-0165_grafana-interim-alerting.md) 決定 3）。
> **この環境では実在する宛先を知らない。** 知らないまま書けば
> **ファイルの見た目は「通知先まで配線済み」になるが、実際には誰にも届かない** ——
> **#546 が問題にした「統制を定めた」と「統制が働いている」の混同**を、こちらから作ることになる。
> 代わりに **「気づく経路は Alerting 画面を見ることだけ」「NFR-21 の 5 分を満たすのは評価の側だけ」**を
> **ファイル冒頭と運用仕様書の両方**へ書く。

### 判断 3: **compose と k8s の 2 か所へ入れる**（片方だけにしない）

k8s は ConfigMap へ inline する既存方針（`grafana.yaml` の datasources と同じ形）に揃える。
**片方だけだと経路 B が無音のまま**であり、#665 が明示している。

### 判断 4: **`alertmanagers.targets` は空のままにする**

**ADR-0006（アラートは Alertmanager を用いる）は改めない**（計画が明示）。
Grafana 内蔵アラートは**暫定の一次検知**であり、**Alertmanager 配備後に閉じる**。
**併存させない条件を運用仕様書へ書く。**

### 判断 5: **暫定経路を閉じる条件を、いま書く**

**暫定は放置されると恒久になる。** 運用仕様書へ 3 条件を明記した（要は
**「Alertmanager 経由で通知が実際に届いたことを 1 件以上確かめた」**）。

**併存させない理由**: 同じ 5 ルールが 2 系統で評価されると**同じ事象に対して 2 通の通知が出る**。
重複は「片方は既知の誤報だ」という運用習慣を生み、**本物の通知を握り潰す方向に働く。**

**消し忘れが CI で表面化するようにした**: 検査器は対象 4 ファイルのいずれかが読めないと fail するため、
**`alerting/` だけ消して検査器を残すと CI が落ちる**。**両方を同時に消すことになる。**

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（#665） | 確かめ方 |
| --- | --- | --- |
| 1 | 作業仕様書を先に作る | 本書 |
| 2 | Grafana provisioning へアラートを足す（5 ルール相当＋通知先） | 差分。**ただし通知先は書かない** —— 判断 2 の★（届かない宛先を書くと「配線した」と読める）。**受け入れ基準に対する意図的な逸脱として、理由を [IADR-0165](../adr/IADR-0165_grafana-interim-alerting.md) 決定 3 に残した** |
| 3 | **compose と k8s の 2 か所**へ入れる | 差分 ＋ **検査器が両方を突合** |
| 4 | Alertmanager 配備後に暫定経路を閉じる条件を運用仕様書へ明記 | `operations.md`（3 条件）＋ 判断 5 |
| 5 | **検証手段を決める**（採らなかった案と理由を IADR へ） | 判断 0 ＋ [IADR-0165](../adr/IADR-0165_grafana-interim-alerting.md) 決定 1 |

### 検証（**採れた案 a の中身**）

新設する `scripts/check-grafana-alerting.js` が次を機械で見る。

1. **ルール数が Prometheus の `alerts.yml` と一致する**（片方だけ増えたら落ちる）
2. **ルール名（`alert:` ↔ `title:`）が 1 対 1 に対応する**（名前の取り違え・写し漏れを止める）
3. **各ルールが参照する `datasourceUid` が、datasources に実在する**（軸 3 の欠落の再発防止）
4. **compose と k8s の inline が同内容である**（二重管理の乖離を止める）
5. **必須キーが揃っている**（`apiVersion` / `groups` ＋ ルールごとの
   `condition` / `data` / `noDataState` / `execErrState`）
   —— **`contactPoints` / `policies` は必須にしない。判断 2 で意図的に書かないと決めたためである。**

**自己試験と変異試験を付ける**（#664 の教訓。**門が効いていることの側も自動回帰にする**）。

#### ★ 門は 2 つある。**1 つの変異で両方を確かめたつもりにならなかった**

| 門 | 発火条件 |
| --- | --- |
| **A** | 対象 4 ファイルのいずれかが読めない |
| **B** | ファイルは読めるが**ルールを 1 件も拾えない**（正規表現が実書式に合っていない型＝§軸 2 の誤りそのもの） |

**最初は門 A の試験（空リポジトリ）だけを書いていた。メタ変異試験で門 B を消したところ、緑のまま通った。**
門 B は**ファイルが揃っている**状態でしか通らないため、専用のフィクスチャが要る。
**さらにそのフィクスチャがルール 0 件以外の違反を踏むと、門 B を消しても「別の理由で exit 1」になり
試験が空振りする**（最初に書いた `groups: []` が必須キー検査に掛かっていた）。**健全側へ寄せ直した。**

#### ★ 変異試験は**実データにも当てる**

フィクスチャだけだと「**実ファイルの書式が正規表現に合っていない**」型の空振りを捕まえられない
（#664 の枝番行 `| 6-b |`、本 issue の `^  - alert:` 決め打ちがまさにこれ）。
**実ファイルから 1 ルールだけ改名して違反が出ること**、**実ファイルの datasource 宣言を落とすと違反が出ること**を
自動回帰にした。

#### メタ変異試験の実測（**5 種すべてが落ちることを確かめた**）

| 変異 | 結果 |
| --- | --- |
| 門 A を削除 | **落ちる** |
| 門 B を削除 | **落ちる**（フィクスチャ修正後。修正前は空振りしていた） |
| `title:` の正規表現を字下げ決め打ちへ厳格化 | **落ちる** |
| self-test の変異ケースを 1 件削除 | **落ちる**（件数だけを見ていると通ってしまうため、**ケース名で確かめている**） |
| `datasourceUid` 実在検査を削除 | **落ちる** |

### CI での起動

**`.github/workflows/` は触らない。** `ci.yml` の **`scripts-tests` ジョブ**が
`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` を走らせ、そこから
`scripts.repo.test.js` の 7 件（実データ検査 ＋ 門 A/B の変異試験）が起動する。
**#672 で追加した `check-openapi-dto-drift` と同じ結線である**（専用ジョブを持たない検査器の既定形）。

### 検証の実行結果（**素の exit code**。`| grep` に吸わせない）

```console
$ for f in scripts/check-*.js; do node "$f" >/dev/null 2>&1 || echo "FAIL $f"; done
（出力なし。27 本すべて exit 0）
$ node scripts/scripts.test.js; echo $?
✓ 358 tests passed
0
```

> **★ 検査器が自分の変更を捕まえた。** IADR への参照行を 2 ファイルへ足したとき、
> **k8s 側だけ字下げを落として**「compose と k8s の inline が同内容ではない」で fail した。
> **二重管理の乖離はこの 1 回で実際に起きた** —— 検査 4 が空論でないことの実例である。

## 射程外

- **Alertmanager の配備そのもの** —— 実環境の判断。**#546 で追跡中。**
- **ADR-0006 の改定** —— 計画が「改めない」と明示。
- **「Grafana が受理するか」の検証** —— **この環境では不可**（判断 0）。**限界を明記して残す。**
- **datasource の compose ↔ k8s 同内容化** —— k8s に `tracesToLogs` / `search.hide` が無い（軸 3 の表）。
  **本 PR が持ち込んだ乖離ではなく**、#665 はアラートの配線を求めている。**別 issue として起票した（#674）。**
  検査器の突合対象を `alerting/` から datasources へ広げるかも、そこで判断する。
