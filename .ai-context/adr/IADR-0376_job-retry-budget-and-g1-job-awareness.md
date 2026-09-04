---
title: IADR-0376 門 G1 は Failed Pod を所有 Job の status で判定し、drift Job の待ち予算は実測した BFF の time-to-ready から決める
type: impl-adr
status: Accepted
related_ids:
  - ADR-0007
  - ADR-0008
  - IADR-0029
  - IADR-0232
  - IADR-0369
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs: []
---

# IADR-0376: 門 G1 は Failed Pod を所有 Job の status で判定し、drift Job の待ち予算は実測した BFF の time-to-ready から決める

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装セッション）

## 起点・関連

- 関連する計画書 ID: `ADR-0007`（CI/CD）／`ADR-0008`（テスト戦略）
- 関連する実装 ADR: `IADR-0029`（適用直後のドリフト即時検出＝本 Job の由来）／
  `IADR-0232`（統合検証を PR から後段へ移した。**PR が緑でも後段が赤ければ退行は入っている**）／
  `IADR-0369`（`keycloak-realm-reconcile` Job の新設。`backoffLimit: 0`）
- 関連する実装仕様書: `.ai-context/specs/20260905_issue-1219_drift-job-retry-and-g1-job-awareness.md`
- 先行記録: `.ai-context/specs/20260829_issue-1055_readiness-wait-excludes-job-pods.md`
- 起点 issue: #1219

## コンテキストと課題

`integration-stack`（develop への push で起動）が**間欠的に**赤い。失敗 3 本
（run 33759232177 / 33865471843 / 33869773915）はいずれも門 `check-stack-ready.js` の
**G1 が同じ形で**落ちている。

```
[G1] microservices-platform/config-drift-postsync-r86s5: Ready ではない（phase=Failed, Ready=False）。
```

診断ダンプ（run 33869773915、`2026-09-04T11:59:58.489Z`）を読むと、Job の Pod は **2 本**在る。

```
config-drift-postsync-r86s5   0/1  Error      112s   ← 1 本目
config-drift-postsync-t84h8   0/1  Completed   36s   ← 2 本目（成功）
```

🔴 **Job は再試行で成功して `Complete` に達しているのに、門が赤くなっていた。**
`restartPolicy: Never` の Job は失敗した attempt の Pod を残すので、G1 の
「`Succeeded` 以外で `Ready != True` なら致命」はその残骸を掴む。
落ちた run でも `Deployment 18/18 が available` であり、成功 run との差は
**Pod 件数だけ**（20 件 / 19 件）である —— **スタックは起きていた。**

なぜ 1 本目が落ちるのか。Job のコンテナログは 11 行すべてが

```
curl: (7) Failed to connect to bff-service port 8080 after 0 ms: Could not connect to server
```

で、**各試行が 0 ms**である。Ready な endpoint が 1 つも無い Service は kube-proxy が即座に
REJECT するため `--connect-timeout 5` は効かない。さらに `curl` の `--max-time` は
**1 試行あたりの上限**であって総時間の上限ではない（後述の実測）。よって実効的な待ち予算は
`--retry 10 × --retry-delay 6 = 60 秒`だけだった。

一方、実測した BFF の time-to-ready は **Job Pod 起動から 51〜82 秒**である
（下限＝最後の readiness 503（11:58:57）、上限＝2 本目の curl が成功した時刻（≤11:59:28）、
Job Pod 起動＝11:58:06）。**60 秒はこの区間の内側に在る。** だから間欠になる。

決めることは 2 つある。

1. 門 G1 が `Failed` な Pod をどう扱うか。
2. Job の待ち予算をいくつにし、**何を根拠に**するか。

## 検討した選択肢

### 1. 門 G1 の `Failed` Pod の扱い

| 案 | 内容 | 評価 |
| --- | --- | --- |
| (a) `Failed` も `Succeeded` と同様に一律で対象外にする | 1 行で直る | **不可。** 落ちた Job・Job に所有されていない Pod まで見逃す。**検知能力を捨てる**（緩和であって修正ではない） |
| (b) Pod のラベル（`job-name`）を持つものを一律で外す | #1055 の待ち側と同じ形 | (a) と同じく**落ちた Job を見逃す**。待ち（原理的に Ready にならない）と判定（壊れているかを見る）では要求が違う |
| (c) 所有 Job の `status.conditions` で分類する | `Complete` なら見逃し、`Failed` なら致命、終端未達は保留 | **採用。** 見逃すのは「成功したことを稼働側で確かめられた」Pod だけ |
| (d) `status.failed < backoffLimit + 1` で「まだ再試行できる」を見る | Job を引かずに済む…わけではない | 残り回数は**これから落ちるかを言わない**。`Complete` に達した事実のほうが強い |

### 2. 終端未達（再試行中）にどう振る舞うか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| (e) 即座に致命 | fail-closed で単純 | **新しい間欠赤を作る。** 門が attempt の狭間に着地すると赤くなる |
| (f) 即座に見逃す | 赤くならない | **これから落ちる Job を緑にする。** (a) と同じ穴 |
| (g) 有界に待ち、期限切れは致命 | 120 秒 | **採用。** 門が走る時点で段 9 の `kubectl wait` が **BFF の Ready を既に証明している**ので、走行中の attempt は数秒で成功するはずである。観測された attempt 間隔 76 秒に対し 120 秒は 1 回分を丸ごと覆う |

### 3. 待ち予算の決め方

| 案 | 内容 | 評価 |
| --- | --- | --- |
| (h) 段 9 の `--timeout=600s` に合わせる | 「整合している」ように見える | **過大。** BFF が本当に死んでいるとき Job が 10 分居座る。しかも段 9 が先に落ちるので Job が主役になることはない |
| (i) 60 → 90 秒 | 最小の変更 | 実測上限 82 秒に対して余裕が 8 秒しかない。**同じ賭けを続ける** |
| (j) 180 秒（`--retry 30 --retry-delay 6`） | 実測上限の約 2.2 倍 | **採用**（下記「理由」） |

## 決定

1. **`evaluatePods(ns, pods, jobs)` は `Failed` な Pod を所有 Job の `status` で分類する**（選択肢 c）。
   純関数 `classifyFailedPod` / `jobTerminalState` に切り出す。

   | Pod | 所有 Job | 判定 |
   | --- | --- | --- |
   | `Succeeded` | —— | 対象外（従来どおり） |
   | `Failed`・所有 Job が `Complete` | 成功済み | **見逃す**（notice を出す。黙って見逃さない） |
   | `Failed`・所有 Job が `Failed` | 予算を使い切った | **致命** |
   | `Failed`・所有 Job が終端未達 | 再試行中 | **保留** → 決定 2 |
   | `Failed`・Job に所有されていない | —— | **致命** |
   | `Failed`・所有 Job を引けない | —— | **致命**（fail-closed） |

   🔴 **`Succeeded` 以外を一律に見逃す形にはしない。** また `jobTerminalState` は
   `type` だけでなく **`status: 'True'` を要求する** —— Kubernetes は条件を `status: 'False'` で
   残すことがあり、type だけで読むと**落ちた Job を成功と読む**。

2. **終端未達は 120 秒だけ有界に待ち、期限切れは致命**（選択肢 g / `waitForJobsToSettle`）。
   待ちに入ったことと待ち切れなかったことは、どちらも出力に残す。

3. **drift Job の待ち予算は `--retry 30 --retry-delay 6` ＝ 180 秒**（選択肢 j）、
   `--max-time` は 240（1 試行あたりの上限）。**数字は実測した BFF の time-to-ready から導く。**

4. **`--max-time` に「総時間の上限」の意味を持たせない。** 宣言の注記を実測に合わせて訂正し、
   `scripts.repo.test.js` が旧い言い回しへの逆行を止める。

5. **検査は `scripts.repo.test.js` に置き、変異試験を対で置く。** 判定を外した
   `check-stack-ready.js` を実際にコンパイルして走らせ、**陰性対照が落ちなくなる**ことを見る
   （＝ 陽性対照が「常に緑を返す実装」でも通ってしまう形になっていないことの証明）。

## 理由

- **`--max-time` は 1 試行あたりの上限である（実測）。** `curl 8.19.0` で対照を取った。
  対象は必ず connection refused になる `127.0.0.1:1`。

  ```
  A retry3 delay2 max-time3 : exit=7 elapsed=14s
  B retry3 delay2 no-maxtime: exit=7 elapsed=14s
  C single attempt          : exit=7 elapsed=3s
  D retry3 delay2 max-time60: exit=7 elapsed=14s
  ```

  overall なら A は 3 秒で切れるはずだが **14 秒**掛かり、`--max-time` 無し（B）と同じだった。
  陽性対照 C が「1 試行は 3 秒掛かる」ことを示す。**以前の注記（「全体上限」）は誤りだった。**

- **180 秒の根拠。** 実測の上限 82 秒に対して約 2.2 倍（旧予算 60 秒の 3 倍）。
  段 9 の readiness 待ち（600 秒）より十分短いので、**BFF が本当に起きない場合は段 9 が先に落ちる**
  —— この Job が検知の主役になることはなく、**予算を伸ばして検知能力を捨ててはいない**。
  決定 2 の 120 秒とも整合する（門の時点で readiness は成立済み ＝ 走行中の attempt は即座に成功する）。

- **「待ち時間を伸ばすだけ」で終わらせない。** 予算だけを直すと、BFF がさらに遅い日に
  **同じ間欠赤が同じ形で戻る**。決定 1・2 は「Job が成功しているのに赤い」という**判定の誤り**を
  直しており、こちらが本体である。予算はその頻度を下げる。

- **`keycloak-realm-reconcile`（`backoffLimit: 0`・`IADR-0369`）は再試行しない**ので、1 度落ちれば
  Job も直ちに `Failed` に達し、**決定 1 の下でも致命のままである**。陰性対照として試験に固定した。

## 結果

- 良い影響:
  - 「Job が成功しているのに赤い」形の間欠赤が消える。**スタックが起きているのに赤い**という、
    門の信頼を最も損なう失敗が無くなる（`IADR-0232` の「後段が赤ければ退行が入っている」が
    意味を持ち続ける前提である）。
  - drift の即時検出（`IADR-0029`）が **1 本目の attempt で成立する**ようになり、
    PostSync フックが本来の「適用直後」に近い時点で走る。
  - `Failed` Pod の扱いが**所有 Job の実状態**に基づくようになり、今後 Job が増えても
    名前の列挙を持たずに正しく判定できる。
- 悪い影響・トレードオフ:
  - 門が Job も引くようになり、namespace ごとに `kubectl get jobs` が 1 回増える。
  - 再試行中に着地した場合、門が最大 120 秒待つ（実測上ここへ入るのは稀。予算 180 秒により
    1 本目で成功するのが通常）。
  - **実クラスタでの確認はできていない**（この環境に Docker が無く k3d を起こせない。#1055 と同じ制約）。
    裏取りは CI の生ログ 4 本と `curl` のローカル実測に依る。**「直った」と言えるのは
    develop 着地後の `integration-stack` が緑で回ってからである。**
