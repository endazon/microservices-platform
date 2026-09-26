---
title: IADR-0165 SLO の暫定通知先は Grafana 統合アラートへ「検知まで」を配線し、宛先は書かない
type: impl-adr
status: Accepted
related_ids:
  - NFR-21
  - ADR-0006
  - IADR-0130
  - IADR-0164
  - IADR-0370
  - IADR-0432
author: claude
created: 2026-08-10
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
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

## ［2026-09-26 追記 / #1577］検査器へ 6 点目を足す —— 式の絞り込みと評価器の組み合わせが発火し得ること

上の「見ていないもの」に**式が正しい結果を返すか（Prometheus 側と同一式である、までしか言えない）**と書いた。
**同一式であることそのものが欠陥だった。** Grafana 版は閾値を `expr` ではなく `conditions[].evaluator` に持つ（#1110）ので、
Prometheus 版の絞り込みの式（`up{job="otel-collector"} == 0`）をそのまま写して評価器 `gt 0` で比べると、
絞り込みの後に残る値は 0 であり `0 > 0` が偽になる —— **`OtelCollectorDown` は本決定の時点から一度も発火し得なかった。**
正常時は式が空になるので `noDataState: NoData` が立ち（IADR-0370 §D の実測 `OtelCollectorDown alerts=1 [NoData]`）、
**鳴る向きが逆になっていた。** 全 20 件を走査して、同型は `ServiceRequestMetricsAbsent`（`== 0 and on (job) …`。`and` は左辺の値 0 を残す）の 1 件だけだった
（走査の結果は作業仕様書 `20260926_1577_grafana-filter-evaluator-never-fires.md`）。

**是正**（compose と k8s の inline の両方。Prometheus 版は変えない）:

| ルール | 式 | 評価器 | noDataState | 理由 |
| --- | --- | --- | --- | --- |
| `OtelCollectorDown` | `up{job="otel-collector"}`（生の値） | `lt 1`（値 0 で真） | `OK` | #1544 の `ResetFloorNoReadyEndpoint` と同じ形。系列の不在は `OtelCollectorUpSeriesAbsent` が拾う（同じ不在で 2 通鳴らさない） |
| `ServiceRequestMetricsAbsent` | `… == bool 0 and on (job) (…)`（途絶で 1・受信中で 0） | `gt 0`（据え置き） | `OK` | 空になるのは「15 分前に受信していた job が無い」（途絶ではない）か系列の不在（`HttpServerMetricsSeriesAbsent` が拾う）。Prometheus 版も空なら鳴らない |

`ServiceRequestMetricsAbsent` を生の値（`rate`）と `lt ε` で比べる案は採らない —— 途絶は `rate == 0` の**厳密な等号**であり、
ε の選び方が新しい閾値を持ち込む。Grafana 11.0.0 の閾値式には等号の評価器が無いので、`bool` で 0 / 1 に直してから比べる。

**検査器の 6 点目**（`scripts/check-grafana-alerting.js` の検査 6）: 各ルールの expr の**最上位の比較**（`bool` なし・片辺が数値リテラル）から
発火側で残り得る値の集合を区間で求め、評価器を満たす値の集合と**交わらなければ**違反にする。
PromQL の優先順位どおり `or` は各辺の和、`and` / `unless` は左辺、`bool` は {0, 1}、ベクタどうしの比較は値を縛らない、と読む。
**偽陰性は受容し偽陽性を出さない側へ倒す**（比較の結果へさらに算術・集約を掛けた形は値を縛らないものとして読む）。
**式・評価器を読めないルールは違反にする**（fail-closed）。判定できたルールが 0 件なら fail する（0 件走査の門）。
写しの両方（compose / k8s inline）を見る。`scripts/scripts.repo.test.js` が実データを #1577 以前の形へ戻す変異で両方の検出を固定する。

**「同型の事故が 2 回起きたら」の条件**: 同型は `OtelCollectorDown`・`ServiceRequestMetricsAbsent` の 2 件が実在し、
#1544 でも `ResetFloorNoReadyEndpoint` が同じ形で書かれかけた（同作業仕様書が回避を明記）。issue 本文も検査器を求めている。
**IADR-0345 決定 5 / IADR-0370 決定 7 の「静的検査器を新設しない」は覆さない** —— あちらは「式が参照する系列が稼働 TSDB に実在するか」
（リポジトリの外の事実）であり、本検査はリポジトリ内の 2 つの宣言（expr と評価器）の自己整合である。軸が違う。

**本追記の後も見ていないもの**: Grafana が受理するか（決定 1）と、値が縛られない式の評価器が妥当か（閾値の大きさ・単位は #1110 の射程）。
**是正後の発火を稼働 Grafana では確かめていない**（稼働クラスタへ当たらない作業である）。配備時の確かめ方は作業仕様書 §未検証。

## ［2026-09-26 追記 / #1595］検査 6 の読み取りを YAML の木へ移し、空集合を健全な範囲で止める

上の追記は「比較の結果へさらに算術・集約を掛けた形は値を縛らないものとして読む」と書いたが、#1588 で**読めない形は報告する**側へ改めている
（#1588 の作業仕様書 §1。本 IADR には追記していなかった）。#1595 はその続きで、**新たな設計判断は無い**ため新しい IADR は起こさない。変えたことは 4 つ。

1. **provisioning を YAML の木として読む。** 行の正規表現は、複数行に続く plain の `expr:`（`expr: up` の次の行の `== 0`）を 1 行目だけで読み、
   `gt 0` と組んでも通していた（既存の偽陰性）。scripts/ は依存を入れずに node だけで走るので YAML ライブラリは使えず、
   **本ファイルの書式が使う部分集合**（ブロック / フローの写像・列、plain の続きと行末コメント、引用符の折り畳み、`|` / `>` の指示子）を読む。
   **部分集合の外（アンカー・タグ・複数文書・タブの字下げ・重複キー・plain の中の「: 」）は写しごとに違反にする**（fail-closed）。
   評価器は `{ type, params }` でも Grafana のエクスポート順 `{ params, type }` でもブロックの写像でも読む。`params` は数だけを受理する（Grafana は `[]float64`）。
2. **決して値を返さない式（空集合）を違反にする。ただし健全に認識できる形だけ。** 空の伝播、同じ式の自己矛盾（`E op c and E op c'`・`E unless E`）、
   照合に使うラベルの矛盾（等号の照合子どうし・ラベルを持たないことが確定した辺）の 3 つ。**完全は目指さない** ——
   それ以外の `and` / `unless` は「値を返し得る」として扱う（見逃し側。偽陽性は出さない）。同型だが健全に判定できない形
   （名前の無い選択子・照合の修飾つきの自己矛盾・NaN だけが残り得る `unless`）は「検証できない」と報告する。射程の一覧は検査器の「6.」の節が正本。
3. **threshold の `expression: $A` は違反。** Grafana の threshold は `$` を剥がさずそのまま refId として引く（剥がすのは reduce / resample だけ。
   grafana/grafana@92b769af の `pkg/expr/threshold.go` / `commands.go` を `gh api` で読んで確かめた）。読み替えて通すと、Grafana が評価できない式を緑にする。
4. **端のケースの評価の誤りを直す**: 「任意の値」を ±Inf を含む閉区間にする、単項の符号を `^` より弱くする（`-2 ^ 2` は -4）、
   `or on (…)` の修飾を剥がす、サブクエリ（関数呼び出しにも付く）・`@` と `offset` のどちらの順も読む、`:offset` / `:bool` で終わる名前を修飾子として読まない、
   `UNVERIFIABLE_ALLOWLIST` の空の理由を違反にする。

**本追記の後も見ていないもの**: 上の追記と同じ（Grafana が受理するか・閾値の妥当性）。NaN は値の集合に入れない（`ne` の評価器で NaN が真になる端は近似として受容する）。
変異試験（検査器の各修正を 1 つずつ戻す 23 通り）はすべて自己試験で落ちることを確かめた（#1595 の作業仕様書 §検証）。

## ［2026-09-27 追記 / #1605］検査 6 の読み方を配備の版（Grafana 11.0.0・Prometheus v2.52.0）の実装に合わせる

#1600（#1595）の監査の残り。**新たな設計判断は無い**ため新しい IADR は起こさない。配備の版のソースを `gh api` で読み（読み取りのみ）、検査器の読み方をそれに合わせた。

1. **plain の数は yaml.v3 の読み方にする。** Grafana 11.0.0 は provisioning を `gopkg.in/yaml.v3`（v3.0.1）で読み、`resolve.go` は `_` を除いてから
   `strconv.ParseInt(·, 0, 64)` にかける —— **0 始まりは 8 進**（`[010]` は 8）で、`0x` / `0o` / `0b` は符号つきでも読み、`08` は浮動小数の 8 になる。
   #1595 は YAML 1.2 core schema で読んでいた（`010` を 10）ので、`> 9` と `lt 010` のような組み合わせを見逃していた（既存の偽陰性）。
2. **`absent(…)` のラベルは Prometheus の `createLabelsForAbsentFunction` と同じに作る。** 照合子を順に読み、同じ名前が等号の後に別の照合子へ出れば落とし、
   サブクエリ・括弧はラベルを持たない。#1595 は等号の照合子をそのままラベルにしていたので、`absent(up{job="a", job!="b"}) and vector(1)` を
   「決して値を返さない」と誤判定していた（許可リストでも黙らせられない誤陽性）。
3. **評価器の型は Grafana 11.0.0 の threshold が受け付ける `gt` / `lt` / `within_range` / `outside_range` だけを通す**（`pkg/expr/threshold.go` の
   `supportedThresholdFuncs`）。`gte` / `lte` / `eq` / `ne` / `*_included` は後の版で足されたもので、11.0.0 は誤りとして拒む。issue では「範囲外・記録のみ」としたが、
   受理する型を狭めるだけで実データ（`gt` / `lt` のみ）に影響しないので直した。
4. YAML の空白の細部（二重引用の行末のエスケープした空白・`|+` / `>+` のファイル末の改行・ブロックスカラーの空白だけの行）を YAML 1.2 のとおりに読む。

**本追記の後も見ていないもの**: 上の追記と同じ。加えて、Grafana の文字列型の欄（`title` / `condition` / `refId` / `datasourceUid`）は yaml.v3 が plain の元の綴りを入れる
（`refId: 010` は "010"）のに、検査器は数として読んでから文字列にする（"8"）。検査器の中では両側が同じ読み方なので鎖の照合はずれない。記録に留める（#1605 の作業仕様書 §未検証）。
