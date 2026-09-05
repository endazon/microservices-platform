---
title: IADR-0399 経路 B の Prometheus inline は compose と「群名・名前・expr・for・severity」で突合する。文面の凝縮は許し、バイト一致は課さない
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - NFR-21
  - FR-10
  - SC-10
  - ADR-0006
  - ADR-0076
  - IADR-0130
  - IADR-0168
  - IADR-0304
  - IADR-0370
  - IADR-0389
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0076_slo-evaluation-target-and-metric-units.md (決定 3)
---

# IADR-0399: Prometheus のアラートルールの経路 A/B パリティを機械で止める（#1246 の取りこぼし）

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: `ADR-0006`（可観測性スタック）／`ADR-0076` 決定 3（`absent` の併設）／`NFR-21`
- 関連する実装 ADR: `IADR-0168`（Grafana provisioning の経路 A/B パリティ。**本 ADR の 1 回目**）／
  `IADR-0130`（0 件走査で緑を返さない）／`IADR-0304`（collector 設定の乖離の 1 回目）／
  `IADR-0370`（`absent` の対象の決め方）／`IADR-0389`（ナレッジ健全性の生産者）
- 関連する実装仕様書: `.ai-context/specs/20260905_issue-1246_prometheus-inline-alert-parity.md`
- issue: #1246（ルールの出所）／#1203（乖離を発見して受容として記録した）

## コンテキストと課題

`deploy/local/observability/prometheus.yaml`（経路 B・k8s）は、compose 側の
`deploy/prometheus/alerts.yml` を **inline で二重管理**している。kustomize が root 外を参照できず、
ConfigMap の `data` の**文字列の内側**へ patch も届かないためであり、
同ファイル冒頭が「二重管理を許容する」と自ら宣言している。

**その inline が遅れた。** #1246 が compose へ足した `knowledge-health-producers` の 2 ルールが
inline に無い状態が **2 世代**続いた。#1203（PR #1286）は乖離を**発見したが、
「#1246 の射程だ」として埋めず、受容として記録した。**

🔴 **問題は「埋め忘れ」ではない。誰も見ていなかったことである。**
`check-grafana-alerting.js` は Grafana 側の inline だけを見ており、Prometheus 側の inline には
**検査器が 1 つも無かった。**

### 同型の事故の数え

| 回 | 事故 | 記録 |
| --- | --- | --- |
| **1** | 経路 B の Grafana に `dashboards/` が**丸ごと無い**ことを誰も見ていなかった。中の `llm-usage.json` は月次 LLM 費用確認 runbook の**行き先**だった | `IADR-0168`（#674）——「**検査していたのに見つからなかったのではなく、検査の射程が狭かった**」 |
| **2** | 経路 B の Prometheus inline が #1246 の 2 ルールに追随せず 2 世代残った | 本件（`deploy/local/observability/prometheus.yaml:26-31` が #1203 の受容として記録していた） |

🔴 **1 回目が門を生んだのに、その門の射程外で 2 回目が起きた。**
これは `IADR-0304` → #1090（collector 設定の自己テレメトリ）で 1 度通った道と同じ形である。

## 検討した選択肢

### 何を突合するか

- **A. バイト一致**（`check-grafana-alerting.js` の検査 4 と同じ）
- **B. 群名 ＋ ルール名 ＋ `expr` ＋ `for` ＋ `severity`**（`summary` / `description` は見ない）
- **C. ルール名だけ**

### どこに置くか

- **D. `check-grafana-alerting.js` を拡張**
- **E. `check-grafana-provisioning-parity.js` を拡張**
- **F. 新設**

### そもそも門を置くか

- **G. 置かない**（1 回目として記録に留める）
- **H. 置く**（2 回目として）

## 決定

### 決定 1: 門を置く（選択肢 H）

規約は「**同型の事故が 2 回起きたら**検査器を足す（1 回目は記録に留める）」と定める。
上の数えのとおり **2 回目**である。1 回目は `IADR-0168` が記録し、門まで生んだ。

🔴 **「1 回目は記録に留める」を機械的に当てて G を採らない。**
数えるべき型は「**経路 B の inline が compose の原本から静かに遅れた**」であって
「Prometheus の inline が遅れた」ではない。**型を狭く取れば何度でも 1 回目になる。**

### 決定 2: 突合するのは群名・ルール名・`expr`・`for`・`severity` だけ（選択肢 B）

🔴 **バイト一致（A）を採らない。経路 B の inline は意図的に文面を凝縮している。**

実測（基点 `4eff9bb4`。共通 10 ルールを機械で突合した）:

| 軸 | 結果 |
| --- | --- |
| 群名 / `expr` / `for` / `severity` | **10 件すべて一致** |
| `summary` | 一致 |
| `description` | **すべて違う**（生のメトリクス名を平叙へ言い換え、末尾の ADR 引用を落としている） |

**バイト一致を課すと初日から赤になり、赤を消すには凝縮をやめるしかない。**
凝縮は経路 B の inline が読まれる文脈（ConfigMap の中）に合わせた設計であり、それを検査器の都合で壊さない。

**ルール名だけ（C）も採らない。** 名前が同じで `expr` が違う乖離を通す ——
それは「同じアラートに見えて別のものを測っている」という、**遅れよりも危険な形**である。

### 決定 3: 新しい検査器として置く（選択肢 F）

| 案 | 判定 |
| --- | --- |
| D. `check-grafana-alerting.js` を拡張 | ❌ **軸が違う。** あちらは「Prometheus のルール ↔ Grafana のルール」の 1 対 1 であり、経路 A/B のパリティではない。同スクリプト冒頭が「本検査器で確かめられないこと」を先に書く作法を持っており、別軸を混ぜるとその宣言が嘘になる |
| E. `check-grafana-provisioning-parity.js` を拡張 | ❌ **名前が嘘になる。** 同スクリプトは basename（ConfigMap の data キー）で突合し、`.yaml` は全行コメント・空行を落として**厳密比較**する作りで、**凝縮を許す比較を持たない** |
| **F. 新設** | ✅ 軸が独立し、名前が中身と一致する。既存 2 本の冒頭が互いに「重ならない別の軸である」と宣言している作法に揃う |

### 決定 4: 残り 4 組の二重管理には門を置かない

宣言された二重管理は **6 組**ある（alertmanager / Grafana datasources / Grafana alerting / loki /
`prometheus.yml` 本体 / `alerts.yml`）。うち機械で守られているのは Grafana alerting だけだった。

🔴 **「同型だから今のうちに全部」を採らない。** それは母集合の取り違えの逆側であり、
**起きていない事故のために門を増やす**ことになる。本 ADR は**実際に遅れた 1 組**に門を置く。
残り 4 組には**同じ形の露出がある**とここに記録し、遅れが 1 回でも観測されたらそのとき判断する。

## 理由

- 決定 1 の数え方が本 ADR の実体である。**型を「経路 B の inline が原本から静かに遅れた」と取ったのは、
  1 回目（`IADR-0168`）が自分でそう書いているからである** ——「検査の射程が狭かった」。
  射程の外で同じことが起きたなら、それは 2 回目である。
- 決定 2 は `check-grafana-provisioning-parity.js` が採った「**緩いより厳しい側へ倒す**」の逆ではない。
  あちらは**同じものを 2 箇所に置く**構図（鍵の順序すら差として報告する）だが、こちらは
  **意図的に別の文面を持つ**構図である。**厳しさの向きは、守るべき不変条件の形で決まる。**
- 決定 4 は「検査器・規約の追加は同型の事故が 2 回起きたら」という規約そのものの適用である。
  規約を守る PR が規約を破らない。

## 結果

- 良い影響: 経路 B の inline が **12 / 12 件**で compose と一致した。以後の乖離は必須 check
  `scripts-tests` が止める（`scripts.repo.test.js` が self-test ＋ 実データ ＋ 変異試験で固定する）。
  検査器の母集合ラチェットが **51 → 52** へ設計どおり発火した。
- 悪い影響 / トレードオフ:
  - **`summary` / `description` の乖離は止まらない。** 凝縮を許した以上、文面が意味ごと食い違っても
    機械では見えない。**これは意図した穴である**（穴を塞ぐには凝縮をやめるしかない）。
  - **検査器が 1 本増えた。** 毎セッション必読の規約ではないが、`scripts-tests` の実行時間は増える。
  - **残り 4 組は依然として無防備である**（決定 4）。

### 実データの確認

```console
$ node scripts/check-prometheus-alerts-parity.js --self-test
  [case] 陽性: 文面だけが違っても違反 0 件
  [case] 変異: k8s からルールを 1 件消すと違反
  [case] 変異: expr を変えると違反
  [case] 変異: for を変えると違反
  [case] 変異: severity を変えると違反
  [case] 変異: 群名を変えると違反
  [case] 変異: k8s にだけ余分なルールがあると違反（逆向きの乖離）
  [case] ブロックスカラーの expr が 1 本に連結される
  [case] extractK8sInline が alerts.yml ブロックだけを取る
[check-prometheus-alerts-parity] self-test OK（9 件）

$ node scripts/check-prometheus-alerts-parity.js
[check-prometheus-alerts-parity] OK: compose 12 件 / k8s inline 12 件のルールが
  群名・名前・expr・for・severity で 1 対 1 に対応しています。

# 変異試験（実データ）: 足した 2 件のうち 1 件を消す
[check-prometheus-alerts-parity] 違反 1 件:
  - 経路 B の inline に 'KnowledgeHealthUnresolvedLinksProducerAbsent'（群 knowledge-health-producers）が無い。

$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
✓ 747 tests passed
```

## 関連

- Supersedes: なし
- Superseded by: なし
- 1 回目の記録: [IADR-0168](./IADR-0168_grafana-provisioning-parity.md)
- 同じ「2 回目で門を足す」形: [IADR-0304](./IADR-0304_alertmanager-deployment-and-null-receiver.md) → #1090（collector の自己テレメトリ宣言）
