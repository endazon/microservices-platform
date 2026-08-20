---
title: 作業仕様書 — Grafana provisioning の compose ↔ k8s 乖離を全数で埋め、機械で突合する（#674）
type: spec
status: done
related_ids:
  - NFR-19
  - ADR-0006
  - IADR-0164
  - IADR-0165
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
---

# 作業仕様書: Grafana provisioning の経路間パリティ（#674）

## 起点

- **NFR-19**（可観測性）。計画 ADR: **ADR-0006**
- 起点 issue: **#674**（**自分で起票した**。出所は #665 / PR #673 の AI レビュー 🟢）

## 母集合（自分で引き直した）

### 軸 1: issue 番号で引く

```console
$ git ls-files -z ':!planning' ':!src/ai-stock-trading' | xargs -0 grep -ln '#674'
docs/adr/IADR-0165_grafana-interim-alerting.md
docs/specs/20260810_issue-665_grafana-alerting.md
```

**2 件。いずれも #665 側からの申し送り**であり、実装指示は無い。

### ★★ 軸 2: **issue の射程は狭すぎた。k8s にはダッシュボードが 1 枚も無い**

#674 は「Tempo の `tracesToLogs` / `search.hide` が k8s に無い」と書いた。**それは氷山の一角だった。**
**issue 自身が「`dashboards/` を含めて引き直せ」と指示している**ので、指示どおり引いた。

**compose 側（`deploy/docker-compose.yml:214` が `./grafana/provisioning` を丸ごとマウント）**:

```
deploy/grafana/provisioning/alerting/slo-alerts.yaml
deploy/grafana/provisioning/dashboards/dashboards.yaml
deploy/grafana/provisioning/dashboards/llm-usage.json
deploy/grafana/provisioning/dashboards/microservices-platform-overview.json
deploy/grafana/provisioning/datasources/datasources.yaml
```

**k8s 側（`deploy/local/observability/grafana.yaml` の ConfigMap ＋ volumeMount）**:

```
grafana-datasources  → datasources.yaml   → /etc/grafana/provisioning/datasources
grafana-alerting     → slo-alerts.yaml    → /etc/grafana/provisioning/alerting
（dashboards のマウントが無い）
```

| provisioning | compose | k8s |
| --- | --- | --- |
| `datasources/` | 有り | 有り（**内容が一部欠落**。軸 3） |
| `alerting/` | 有り | 有り（#665 で入れた。**同内容を機械検査中**） |
| **`dashboards/`** | **3 ファイル** | **★ 丸ごと無い** |

### ★★ 軸 3: **欠けているダッシュボードは #546 の月次確認の行き先である**

```console
$ grep -n 'llm-usage' docs/operations/llm-cost-monthly-review-runbook.md
59:2. ダッシュボード **`LLM Usage (proxy for cost — NOT cost)`**（uid: `llm-usage`）を開く。
60:   実体は deploy/grafana/provisioning/dashboards/llm-usage.json。
```

**[IADR-0164](../adr/IADR-0164_llm-cost-monthly-review-interim-control.md)（#546）は「LLM 費用の暫定統制は
月次の手動確認」と定め、その行き先として `llm-usage.json` を作った。**
**経路 B（ローカル k8s dev）では、その行き先が存在しない。**

> **★ #546 が問題にした型そのものである。** 「統制を定めた」と「統制が働いている」の混同 ——
> **手順書は行き先を指しているが、片方の経路ではそこへ行けない。**
> **#665 の判断（compose と k8s の 2 か所へ入れる。片方だけだと経路 B が無音）と同じ理由**で埋める。

### 軸 4: **datasource の欠落を 1 件ずつ**

| キー | compose | k8s | 影響 |
| --- | --- | --- | --- |
| Tempo `jsonData.tracesToLogs`（`datasourceUid: loki` / `spanStartTimeShift` / `spanEndTimeShift`） | 有り | **無し** | **トレースからログへ辿れない** |
| Tempo `jsonData.search.hide: false` | 有り | **無し** | 既定値と同じため実害は小さい |
| Tempo `jsonData.serviceMap.datasourceUid` | 有り | 有り | —— |
| Tempo `jsonData.nodeGraph.enabled` | 有り | 有り | —— |
| Prometheus `jsonData.timeInterval` / `isDefault` | 有り | 有り | —— |

**`tracesToLogs` はブロックごと存在しない。** #665 で `uid` を宣言したが、
**k8s 経路では参照そのものが無いので繋がらない**（IADR-0165 決定 2 の表に記録済み）。

## 判断

### 判断 1: **3 つの乖離をすべて埋める**（片方だけにしない）

| # | 乖離 | 是正 |
| --- | --- | --- |
| 1 | `dashboards/` が k8s に無い | **`grafana-dashboards` ConfigMap を新設**し、`dashboards.yaml` ＋ JSON 2 枚を inline してマウント |
| 2 | Tempo `tracesToLogs` が k8s に無い | compose と同じブロックを足す |
| 3 | Tempo `search.hide` が k8s に無い | 同上 |

**inline の実費を確かめた**: JSON 2 枚 ＋ yaml は **7.2 KB**（`llm-usage.json` 5.2 KB ／
`microservices-platform-overview.json` 1.8 KB ／ `dashboards.yaml` 197 B）。
`grafana.yaml` は 15 KB → **約 22 KB** になる。**datasources / alerting と同じ既存方針で収まる。**

### ★ 判断 2: **検査を `alerting/` から provisioning 全体へ広げる**

#665 で入れた `scripts/check-grafana-alerting.js` は **`alerting/` だけ**を突合していた。
**だから `dashboards/` の丸ごと欠落を誰も見ていなかった。**

**`scripts/check-grafana-provisioning-parity.js` を新設**し、
**compose の provisioning 配下の全ファイルが k8s の ConfigMap に同内容で存在する**ことを検査する。

**`check-grafana-alerting.js` は残す** —— あちらは
**「Prometheus の `alerts.yml` と Grafana のルールが 1 対 1」**という別の軸を見ており、
本検査器（経路間パリティ）とは重ならない。

> **★ 「検査していたのに見つからなかった」ではなく「検査の射程が狭かった」。**
> **#665 の検査器は自分が書いた。** 射程を `alerting/` に絞ったのも自分である。

### 判断 3: **`dashboards.yaml` の `path` は経路で変えない**

compose の `dashboards.yaml` は `path: /etc/grafana/provisioning/dashboards` を指す。
**k8s でも同じパスへマウントする**ので、**ファイルは 1 文字も変えずに inline できる。**
**変えると「同内容」の検査が成り立たなくなる。**

### 判断 4: **`search.hide` も埋める**（実害は小さいが、差を残さない）

**既定値と同じなので挙動は変わらない。** それでも埋める理由は
**「同内容である」を機械検査の対象にするから**である。**例外を 1 つ許すと検査が書けない。**

### ★ 判断 5: **同名の basename は「限界」として書かず、検出して落とす**（レビュー 🟡 の是正）

突合は **basename**（ConfigMap の data キー）で行う —— ConfigMap の data はディレクトリを持たないため、
`dashboards/x.yaml` と `datasources/x.yaml` を k8s 側で区別できない。
**初版はフラットな Map を作っており、同名があると後勝ちで上書きされ、片方の突合が黙って空振りしていた**
（再現して確認した。現状の 5 ファイルは名前が違うので実害は無い）。

**レビューは「限界をコメントに一言残せば安全」と提案したが採らなかった** ——
**「黙って空振りする検査器」は #664 と本 issue が是正した型そのもの**であり、
**その型を是正する PR が新しい同型を持ち込むわけにはいかない。**
k8s 側・compose 側の両方で**同名を検出して違反にする**（[IADR-0168](../adr/IADR-0168_grafana-provisioning-parity.md) 決定 3）。

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（#674） | 確かめ方 |
| --- | --- | --- |
| 1 | 作業仕様書を先に作る | 本書 |
| 2 | **母集合を引き直す**（`dashboards/` を含む） | §軸 2。**issue の射程を超える欠落を見つけた** |
| 3 | どちらへ寄せるか決める | **compose へ寄せる**（k8s に足す）。compose が完全な側である |
| 4 | 検査器の対象を広げるか決める | 判断 2（**新設**。`check-grafana-alerting.js` とは軸が違うので残す） |

### 検証

- `node scripts/check-grafana-provisioning-parity.js`（自己試験 ＋ 実データ）
- **k8s の YAML が妥当**であること（ConfigMap 3 枚 ＋ Deployment ＋ Service を解析）
- **inline したダッシュボード JSON が、元ファイルと 1 バイトも違わない**こと
- **門は 2 つ**（compose 側 0 件走査 ／ k8s 側 0 件走査）**を別々に変異試験する**

## 射程外

- **Grafana が受理するか** —— この環境では起動できない（[IADR-0165](../adr/IADR-0165_grafana-interim-alerting.md) 決定 1）。**限界は同じ。**
- **stg / prod（Helm）への展開** —— `deploy/helm/` に Grafana リソースは無い。**#546 で追跡中。**
- **ダッシュボードの中身** —— 本 PR は**経路間で同じものが在ること**だけを見る。
