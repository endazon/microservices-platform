---
title: IADR-0168 Grafana provisioning は経路間で同内容とし、突合を `alerting/` から全体へ広げる
type: impl-adr
status: Accepted
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

# IADR-0168: Grafana provisioning の経路間パリティ（#674）

- 状態: Accepted
- 日付: 2026-08-10
- 決定者: claude（実装）

## 起点・関連

- **NFR-19**（可観測性）。計画 ADR: **ADR-0006**
- 実装 issue: **#674**（**自分で起票した**。出所は #665 / PR #673 の AI レビュー 🟢）
- 作業仕様書: [20260810_issue-674](../specs/20260810_issue-674_grafana-provisioning-parity.md)

## ★★ 文脈 —— **issue の射程が狭すぎた。k8s にはダッシュボードが 1 枚も無かった**

#674 は「Tempo の `tracesToLogs` / `search.hide` が k8s に無い」と書いた。
**issue 自身が「`dashboards/` を含めて引き直せ」と指示していた**ので指示どおり引いたところ、
**`dashboards/` が k8s へ丸ごとマウントされていない**ことが分かった。

| provisioning | compose | k8s（是正前） |
| --- | --- | --- |
| `datasources/` | 有り | 有り（**内容が一部欠落**） |
| `alerting/` | 有り | 有り（#665 で入れた） |
| **`dashboards/`（3 ファイル）** | **有り** | **★ 丸ごと無い** |

### **欠けていたのは #546 の月次確認の行き先である**

[IADR-0164](./IADR-0164_llm-cost-monthly-review-interim-control.md)（#546）は
「LLM 費用の暫定統制は**月次の手動確認**」と定め、その行き先として `llm-usage.json` を作った。
Runbook は `uid: llm-usage` のダッシュボードを開けと書いている。

**経路 B（ローカル k8s dev）には、そのダッシュボードが存在しなかった。**

> **★ #546 が問題にした型そのものである。** 「統制を定めた」と「統制が働いている」の混同 ——
> **手順書は行き先を指しているが、片方の経路ではそこへ行けない。**

## 決定 1: **3 つの乖離をすべて埋める**（compose へ寄せる）

| # | 乖離 | 是正 |
| --- | --- | --- |
| 1 | `dashboards/` が k8s に無い | `grafana-dashboards` ConfigMap を新設し、3 ファイルを inline してマウント |
| 2 | Tempo `jsonData.tracesToLogs` が k8s に無い | compose と同じブロックを足す |
| 3 | Tempo `jsonData.search.hide` が k8s に無い | 同上 |

**compose が完全な側**なので compose へ寄せる。**inline の実費は 7.2 KB**（`grafana.yaml` は 15 → 22 KB）で、
**datasources / alerting と同じ既存方針に収まる**ことを実測してから決めた。

**`dashboards.yaml` の `path` は経路で変えない。** 同じ `/etc/grafana/provisioning/dashboards` へ
マウントするので**ファイルを 1 文字も変えずに inline できる** —— 変えると「同内容」の検査が成り立たない。

**`search.hide` は既定値と同じで挙動は変わらないが、それでも埋める。**
**例外を 1 つ許すと「同内容である」を機械検査できなくなる。**

## ★★ 決定 2: **突合の射程を `alerting/` から provisioning 全体へ広げる**

#665 で入れた `scripts/check-grafana-alerting.js` は **`alerting/` だけ**を突合していた。
**だから `dashboards/` の丸ごと欠落を誰も見ていなかった。**

**`scripts/check-grafana-provisioning-parity.js` を新設**し、
**compose の provisioning 配下の全ファイルが k8s に同内容で存在し、かつマウントされている**ことを見る。

> **★ 「検査していたのに見つからなかった」のではなく「検査の射程が狭かった」。**
> **#665 の検査器は自分が書いた。射程を `alerting/` に絞ったのも自分である。**

**`check-grafana-alerting.js` は残す。** あちらは **Prometheus の `alerts.yml` と Grafana のルールが
1 対 1** という別の軸を見ており、本検査器（経路間パリティ）と重ならない。

### 検査するもの / しないもの

| 見る | 見ない |
| --- | --- |
| compose の全ファイルが k8s の ConfigMap にあるか | **Grafana が受理するか**（[IADR-0165](./IADR-0165_grafana-interim-alerting.md) 決定 1。この環境では起動できない） |
| 内容が同じか（`.json` は鍵順非依存、`.yaml` はコメント・空行を除いて比較） | ダッシュボードの中身が正しいか |
| ConfigMap が実際に **volumeMounts されているか**（宣言だけの死んだ資源を止める） | stg / prod（Helm）—— Grafana リソースが無い。#546 で追跡中 |
| k8s にだけある inline（経路 A が無音になる同型） | |

### 比較の限界を書いておく

**YAML パーサを使わない**（本リポの検査器は Node 標準のみで動かす方針で、`js-yaml` は解決できない。実測）。
**全行コメント・空行・行末空白を落として比較する。**
**限界**: 鍵の順序を入れ替えただけでも「不一致」と報告する ——
**緩いより厳しい側へ倒している**（本物の乖離を素通りさせない）。

## ★★ 決定 3: **同名の basename は「限界」として書かず、検出して落とす**

**AI レビュー 🟡 の指摘**（PR #678）。突合は **basename**（ConfigMap の data キー）で行う ——
ConfigMap の data はディレクトリを持たないため、`dashboards/x.yaml` と `datasources/x.yaml` は
k8s 側で区別できない。**初版はフラットな Map を作っており、同名があると後勝ちで上書きされ、
片方の突合が黙って空振りしていた。**

**再現して確かめた**（`extractInlineFiles` に同名を 2 つ与えると後の値だけが残る）。
**現状の 5 ファイルはすべて名前が違うので実害は無い。**

**レビューは「限界を一言コメントに残せば安全（ブロッキングではない）」と提案したが、採らなかった。**

> **「黙って空振りする検査器」は #664 と本 issue（#674）が是正した型そのものである。**
> **その型を是正する PR が、新しい同型を持ち込むわけにはいかない。**
> **コメントは読まれないことがあるが、fail するテストは読まれる。**

**k8s 側・compose 側の両方で同名を検出して違反にする。**
`extractInlineFiles` は**上書きせず** `duplicates` へ記録し、`findIssues` がそれを違反として返す。

### ★ 追記: **曖昧なまま比較して「乖離している」と断定しない**

**AI レビュー 🟢**（同 PR の 2 回目）。同名があるとき、主ループが**どちらか一方**の inline と比較して
「`provisioning/alerting/x.yaml` が compose と k8s で同内容でない」と報告していた。
**レビューは「出力が賑やかになるだけで実害はない」と評したが、そうではない** ——
**対応付けが決まっていないのに、特定のファイルが乖離していると断定している。**

> **黙るより悪い。** 読む人はその 1 行を根拠に、実際には比べていないファイルを直しに行く。

**同名の名前については比較そのものを行わず、曖昧さだけを報告して降りる。**
**「断定しないこと」を自己試験で固定した**（`同内容でない` を含まないことを assert する）。

## ★ 決定 4: **自己試験が、自分の書いた主張の誤りを捕まえた**

`.json` の比較を「鍵順・空白を無視する」と書いたが、
**`JSON.stringify(JSON.parse(x))` は挿入順を保つ**ため実際には鍵順に依存していた。
**その主張を書いた自己試験が落ちて気づいた。**

**鍵を並べ替えて安定化する `canonicalJson()` を足し、主張のほうへ実装を合わせた。**

> **★ 「テストが実装に合っていない」のではなく「実装が自分の主張に合っていない」場合がある。**
> **落ちたテストを緩める前に、どちらが正しいかを決める。**

## 結果

- `deploy/local/observability/grafana.yaml`（`grafana-dashboards` ConfigMap 新設 ＋ マウント ＋ Tempo の 2 キー）
- `scripts/check-grafana-provisioning-parity.js`（新規。自己試験 **13 件**）
- `scripts/scripts.repo.test.js`（7 件追加。**門 A / 門 B を別々に変異試験**）
- `docs/operations/operations.md`（経路 B でダッシュボードが見られるようになったこと）

### **直したものを捕まえるか、実データで確かめた**

**develop 時点の k8s マニフェストを取り出して当て、4 件の乖離を検出することを実測した。**

```
- compose の provisioning/dashboards/dashboards.yaml が k8s の ConfigMap に無い（経路 B に存在しない）
- compose の provisioning/dashboards/llm-usage.json が k8s の ConfigMap に無い（経路 B に存在しない）
- compose の provisioning/dashboards/microservices-platform-overview.json が k8s の ConfigMap に無い（経路 B に存在しない）
- provisioning/datasources/datasources.yaml が compose と k8s で同内容でない
```

**この試験は `scripts.repo.test.js` へ常設した**（`git show origin/develop:` で取り出す。
本 PR がマージされて develop 側にも是正が入ったら、前提が消えるので試験は自動で飛ぶ）。

**メタ変異試験（6 種すべてが落ちる）**: 門 A 削除 ／ 門 B 削除 ／ 欠落検出を無効化 ／
内容比較を常に真へ ／ マウント検査を無効化 ／ inline 抽出の字下げを決め打ち。

### フォローアップ

1. **Grafana が受理するか** —— この環境では確かめられない（[IADR-0165](./IADR-0165_grafana-interim-alerting.md) 決定 1）。
   **配備時に経路 B でダッシュボード 2 枚が表示されることを確かめる。**
2. **Tempo の traces-to-logs が実際に繋がるか** —— 宣言を揃えただけで、連携の復活は未検証
   （[IADR-0165](./IADR-0165_grafana-interim-alerting.md) フォローアップ 2 を引き継ぐ）。
3. **stg / prod（Helm）** —— Grafana リソースが無く、本検査器の対象外。**#546 で追跡中。**
