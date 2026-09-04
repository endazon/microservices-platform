---
title: integration-stack の間欠赤: 再試行で成功した Job の残骸 Pod を門 G1 が致命扱いする（#1219）
type: spec
status: done
related_ids: [NFR-09, ADR-0007, ADR-0008, IADR-0029, IADR-0232, IADR-0369, IADR-0377]
author: Claude
created: 2026-09-05
updated: 2026-09-05
plan_refs: []
---

# #1219: 「再試行で成功した Job の失敗 Pod」を門 G1 が致命にしている

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR）。CI/CD の門の健全性。
- 関連 ADR: `ADR-0007`（CI/CD）・`ADR-0008`（テスト戦略）。`ADR-0021`（エッジ・実行基盤）は間接。
- 先行記録: `.ai-context/specs/20260829_issue-1055_readiness-wait-excludes-job-pods.md`（待ちから Job Pod を外した）／
  [IADR-0232](../adr/IADR-0232_ci-pr-latency-reduction.md)（PR から後段へ移した）／
  [IADR-0369](../adr/IADR-0369_persist-by-default-and-realm-reconcile-job.md)（`keycloak-realm-reconcile` Job の新設）。

## 1. 事象

`integration-stack`（develop への push で起動）が**間欠的に**赤くなる。恒常的な退行ではない。

| run | develop | 結果 |
| --- | --- | --- |
| 33759232177 | `7588d2ba` | **failure** |
| 33865471843 | `c15f4696` | **failure** |
| 33869773915 | `888e307d` | **failure** |
| 33884138720 / 33888652928 / 33889232347 | 以降 | success |

失敗はすべて門 `check-stack-ready.js` の **G1** で、**まったく同じ形**である。

```
[G1] microservices-platform/config-drift-postsync-r86s5: Ready ではない（phase=Failed, Ready=False）。
```

## 2. 実測（生出力）—— 仮説の検証

### 2-1. 落ちた run では Job Pod が 2 つ在り、**2 つめは成功している**

`gh run view 33869773915 --log` の診断ダンプ（`kubectl get pods -o wide`、`2026-09-04T11:59:58.489Z`）:

```
microservices-platform   bff-service-5868ff7954-4q9x8    1/1  Running    1 (93s ago)  110s
microservices-platform   config-drift-postsync-r86s5     0/1  Error      0            112s
microservices-platform   config-drift-postsync-t84h8     0/1  Completed  0            36s
```

3 本の失敗 run すべてで同じ形である。

| run | 1 本目（Error）の age | 2 本目（Completed）の age | 差 |
| --- | --- | --- | --- |
| 33869773915 | 112s (`r86s5`) | 36s (`t84h8`) | 76s |
| 33865471843 | 109s (`gjjmn`) | 33s (`f9j89`) | 76s |
| 33759232177 | 111s (`kpfd6`) | 34s (`sz2c8`) | 77s |

🔴 **Job は再試行で成功して `Complete` に達している。** 門が落ちたのは
`restartPolicy: Never` が残す **1 本目の `Failed` Pod** に対してである。

**Job が既に終端（Complete）だったことの証拠**: 門の出力（`11:59:58.290Z`）は `r86s5` **だけ**を挙げ、
`t84h8` を挙げていない。`t84h8` が実行中なら `Ready=False` で同じく挙がるので、
**門が Pod を読んだ時点で `t84h8` は既に `Succeeded`** だった。

さらに Deployment の判定は健全である —— 落ちた run も
`microservices-platform: Deployment 18/18 が available` であり、
成功 run（33884138720）との差は **Pod 件数だけ**（失敗 20 件 / 成功 19 件。差分 ＝ 残骸 1 本）。

### 2-2. 1 本目が落ちた理由 —— BFF の readiness に間に合っていない

`r86s5` のコンテナログ（同 run、`12:00:00.686Z` の診断ダンプ）:

```
curl: (7) Failed to connect to bff-service port 8080 after 3 ms: Could not connect to server
curl: (7) Failed to connect to bff-service port 8080 after 0 ms: Could not connect to server
（同型が計 11 行 ＝ 初回 1 ＋ --retry 10）
```

🔴 **各試行が 0〜3 ms で終わっている。** Ready な endpoint が 1 つも無い Service は
kube-proxy が即座に REJECT するため、`--connect-timeout 5` は**一度も効かない**。

### 2-3. `--max-time` は「リトライ総時間の上限」ではない（実測）

宣言の注記は「全体上限（`--max-time`）をリトライ総時間より長く取る」と書いているが、**誤りである**。
`curl 8.19.0` で対照を取った（対象は必ず connection refused になる `127.0.0.1:1`）:

```
A retry3 delay2 max-time3 : exit=7 elapsed=14s
B retry3 delay2 no-maxtime: exit=7 elapsed=14s
C single attempt          : exit=7 elapsed=3s
D retry3 delay2 max-time60: exit=7 elapsed=14s
```

`--max-time 3` を付けても **14 秒**掛かった（＝ 4 試行 × 3s ＋ 3 待機 × 2s）。
overall なら 3 秒で切れるはずなので、**`--max-time` は 1 試行あたりの上限**である。
陽性対照 C（単発 3 秒）が「1 試行は 3 秒掛かる」ことを示し、A＝B＝D が
「`--max-time` は総時間に関与しない」ことを示す。

したがって現行宣言の**実効的な待ち予算は次のとおり**で、`--max-time 180` は**一度も参加しない**。

```
--retry 10 --retry-delay 6  →  10 × 6 秒 = 60 秒
（各試行は 0 ms なので試行時間の寄与は無い）
```

Pod の実寿命とも整合する: `r86s5` は 11:58:06 起動 → 2 本目が 11:59:22 起動（差 76 秒）。
Job の初回バックオフ 10 秒を引くと **1 本目の寿命 ≒ 66 秒 ＝ 60 秒の予算 ＋ コンテナ起動 ≒ 6 秒**。

### 2-4. BFF の time-to-ready

同 run のタイムライン（すべて診断ダンプの age / event age から起こした絶対時刻）:

| 時刻 | 事象 | 出所 |
| --- | --- | --- |
| 11:58:06 | Job Pod `r86s5` 起動（helm `[6/7]` は 11:58:06.19） | age 112s |
| 11:58:08 | `bff-service` Pod 起動 | age 110s |
| 11:58:25 | `bff-service` が 1 回目の再起動（liveness kill） | `RESTARTS 1 (93s ago)` |
| 11:58:57 | `bff-service` の readiness が最後に 503 | event age 61s |
| 11:59:12 頃 | `r86s5` が予算切れで Error | 76s − backoff 10s |
| 11:59:22 | Job Pod `t84h8` 起動 → **成功** | age 36s |
| 11:59:46 | up が EXIT=0 で戻る | ステップ境界 |
| 11:59:50 | 門 `check-stack-ready.js` 開始 | ステップ境界 |
| 11:59:58 | 門が G1 で失敗 | 出力 |

BFF が Ready になったのは **`[11:58:57, 11:59:28]`** の区間（下限＝最後の 503、
上限＝2 本目の curl が成功した時刻）。Job Pod 起動（11:58:06）からの経過は **51〜82 秒**である。

🔴 **現行予算 60 秒はこの区間のど真ん中に在る。** だから間欠になる。
成功 run（33884138720）は Job Pod が 1 本しか無い（`Pod 19 件`）＝ 初回で通っており、
**BFF が 60 秒未満で Ready になった run と、そうでない run が混ざっている**ことが実測で確かめられた。

### 2-5. 私の一次仮説に対する訂正

| 仮説 | 実測 |
| --- | --- |
| 再試行の総予算が約 60 秒しかなく、BFF を待ち切れない | **正しい**（2-2 / 2-3 / 2-4） |
| `--max-time 180` との辻褄 | **前提が誤り**。`--max-time` は per-attempt であり総予算に関与しない（2-3） |
| G1 が「これから再試行される Job Pod の Failed」を致命にしている | **不正確**。実測ではもっと悪く、**再試行が既に成功して Job が `Complete` に達した後の残骸**を致命にしている（2-1） |
| BFF は 95 秒前後まで readiness が通っていない | **過大**。上限 82 秒、下限 51 秒（2-4） |

## 3. 母集合（規則 1・2・9・10）

誤りの側＝**「Job の Pod が残ることに依存する判定・待ち」**で走査した。

| 走査 | 結果 |
| --- | --- |
| `deploy/` 配下の `kind: Job` / `kind: CronJob` | **2 件**: `templates/drift-postsync-job.yaml`（`microservices-platform` / `backoffLimit: 3`）、`deploy/local/keycloak-setup/realm-reconcile-job.yaml`（`platform-infra` / `backoffLimit: 0`） |
| Pod の Ready を判定する箇所（`Succeeded` / `PodCompleted` で走査） | **1 件**: `scripts/check-stack-ready.js` の `evaluatePods`（G1） |
| readiness を `kubectl wait` で待つ箇所 | **1 件**: `.github/workflows/integration-stack.yml` 段 9（`--timeout=600s`） |
| BFF の起動を `--retry` で待つ宣言 | **1 件**: `drift-postsync-job.yaml` |
| 予算の導出値を書いている記述（規則 10 で引き直し） | **3 件**: `drift-postsync-job.yaml` の注記 2 行（「全体上限」「10 回 × 6 秒」）、`values.yaml:185`（「起動待ちは `--retry` で吸収する」） |

**除外と理由**:

- `deploy/argocd/appproject.yaml` の `kind: Job` —— **許可リストの列挙**であって Job の宣言ではない。
- `.github/workflows/integration-stack.yml:228` の `status.phase!="Succeeded"` jsonpath ——
  **診断ダンプの絞り込み**であって合否判定ではない（ワークフロー冒頭の「判定は check-stack-ready.js に集約」）。
- `kube-system` の `helm-install-traefik-*` Pod —— 門の対象名前空間（`NAMESPACES`）に無い。
- `scripts/scripts.repo.test.js:3813` の `.Succeeded` —— C# の `AuthorizationResult`。無関係。

🔴 **`keycloak-realm-reconcile` は `backoffLimit: 0` なので再試行しない。**
1 度落ちれば Job も直ちに `Failed` になるため、本作業の変更後も**致命のまま**である（4-2 の陰性対照）。

## 4. 直し方

### 4-1. 門 G1 を Job 対応にする（`scripts/check-stack-ready.js`）

`evaluatePods` に **その名前空間の Job 一覧**を渡し、`Failed` な Pod は所有 Job の状態で判定する。

| Pod | 所有 Job | 判定 |
| --- | --- | --- |
| `Succeeded` | —— | 対象外（現行どおり） |
| `Failed`・所有 Job が `Complete` | 成功済み | **見逃す**（notice を出す。＝本件） |
| `Failed`・所有 Job が `Failed` | 予算を使い切った | **致命** |
| `Failed`・所有 Job が終端未達 | 再試行中 | **保留**（呼び出し側が有界に待ち、期限切れは致命） |
| `Failed`・Job に所有されていない | —— | **致命** |
| `Failed`・所有 Job が見つからない | —— | **致命**（fail-closed） |
| その他で `Ready != True` | —— | **致命**（現行どおり） |

🔴 **`Succeeded` 以外を一律に見逃す形にはしない。** 見逃すのは
「**所有 Job が成功で終端に達したことを稼働側の `status` で確かめられた**失敗 Pod」だけである。

「保留」の有界待ち（`waitForJobsToSettle`）は **120 秒**で切る。根拠:
**門が走る時点で段 9 の `kubectl wait` が BFF の Ready を既に証明している**ので、
再試行中の attempt は数秒で成功するはずである。観測された attempt 間隔は 76 秒なので、
120 秒は 1 回分の attempt ＋ backoff を丸ごと覆う。期限切れは**致命**（fail-closed）。

### 4-2. Job 側の待ち予算を実測から決める（`drift-postsync-job.yaml`）

```
--retry 30 --retry-delay 6   →   30 × 6 秒 = 180 秒
--max-time 240               →   1 試行あたりの上限（2-3 の実測に合わせて注記を訂正）
```

**なぜ 180 秒か（実測からの導出）**:

- 実測した BFF の time-to-ready は Job Pod 起動から **51〜82 秒**（2-4）。
- 上限 82 秒に対して **約 2.2 倍**の余裕を取る。現行 60 秒の 3 倍。
- 段 9 の readiness 待ち（`--timeout=600s`）より**十分短い**。
  BFF が本当に起きない場合は段 9 が先に落ちるので、**この Job が検知の主役になることはない**
  ＝ 予算を伸ばして検知能力を捨ててはいない。
- 4-1 の有界待ち 120 秒とも整合する（4-1 の理由により、門の時点では即座に成功する）。

`--max-time` は 1 試行の上限なので、**予算の 180 秒を跨いで効かせることはできない**。
240 秒に上げるのは「per-attempt であっても総予算より短く見える値を置かない」ためで、
数字の意味は 2-3 の実測とともに宣言へ書く。

## 5. 受け入れ基準

- [ ] `node scripts/check-stack-ready.js --self-test` が通り、**陽性・陰性の対**を含む
- [ ] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通る
- [ ] 再試行で成功した Job の残骸 `Failed` Pod は **G1 で緑**（陽性側の対照）
- [ ] `backoffLimit` を使い切って `Failed` に達した Job の Pod は **G1 で赤**（陰性対照）
- [ ] Job に所有されていない `Failed` Pod は **G1 で赤**（陰性対照）
- [ ] 変異試験: 判定を外すと陰性対照が落ちる
- [ ] `helm template` が通り、待ち予算が 180 秒であることが描画結果に現れる
- [ ] `node scripts/check-doc-*.js` が通る

## 6. テスト方針

- 純関数 `evaluatePods(ns, pods, jobs)` / `classifyJobOwnedPod(pod, jobs)` に対し、
  4-1 の表の 7 行を**そのままケースにする**。
- `scripts/scripts.repo.test.js` の integration-stack 節へ、`drift-postsync-job.yaml` の
  待ち予算が宣言から読めることと、`--max-time` に総予算の意味を持たせていないことを固定する。
- **変異試験**: `classifyJobOwnedPod` の Job 状態判定を「常に benign」へ差し替えると
  陰性対照（Job が `Failed`）が落ちることを、テスト内で実際に走らせて確かめる。

## 7. 実測できないこと

**この環境に Docker が無く k3d クラスタを起こせない**（先行記録 #1055 と同じ制約）。
本作業の裏取りは **CI の生ログ**（3 本の失敗 run ＋ 成功 run）と、
`curl` の `--max-time` 意味論の**ローカル実測**（2-3）に依る。
実クラスタでの確認は develop 着地後の `integration-stack` が担う。

## 8. 計画書との差異

- 差異: なし

## 9. 未決事項

- なし
