---
title: readiness 待ちから Job の Pod を外す（#1055）
type: spec
status: done
related_ids: [NFR-09, IADR-0029]
author: Claude
created: 2026-08-29
updated: 2026-08-29
---

# #1055: 「決して Ready にならない Pod」を待つのをやめる

## 1. 事象と原因

Integration Stack 段 9 が 16 件のタイムアウトで落ちた（run 33234971195）。
**同 run の診断ダンプでは、その 16 件を含む全アプリ Pod が `1/1 Running`** である。
**壊れていたのは待ち方であってスタックではない。**

```sh
# .github/workflows/integration-stack.yml:121（変更前）
kubectl -n "$ns" wait --for=condition=Ready pods --all --timeout=600s
```

🔴 **`--all` は Job の Pod を含む。** `config-drift-postsync` は `kind: Job` であり、
**完了した Job の Pod は `Ready=False`（reason `PodCompleted`）で固定され、二度と
`Ready=True` にならない。** `--for=condition=Ready` はこの Pod に対して**原理的に成立しない**。

タイムアウト一覧が**アルファベット順に `config-drift-postsync` から始まり以降すべて**なのは、
この Pod で期限を使い切ったことと整合する。

### 間欠だった理由（競合）

| 値 | 出所 |
| --- | --- |
| `ttlSecondsAfterFinished` | **300 秒**（`drift-postsync-job.yaml`） |
| 待ちの `--timeout` | **600 秒** |

**待ち開始時点で Job の Pod が残っているか**が競合である。TTL で先に消えていれば対象集合に
入らず通り、残っていれば必ず落ちる。**放置すれば再発する。**

## 2. 母集合（規則 1・2）

**誤りの側＝「待ちの対象に入ってしまう Job」**で走査した。

| 走査 | 結果 |
| --- | --- |
| `deploy/` 配下の `kind: Job` / `kind: CronJob` | **1 件**（`drift-postsync-job.yaml`） |
| 待ちが掛かる名前空間 | `platform-infra` / `microservices-platform` の 2 つ |
| 同じ `wait --all` の書き方が他にあるか | 段 9 の 1 箇所のみ |

**除外**: `kube-system` の `helm-install-traefik-*`（Job の Pod）は**待ちの対象名前空間に無い**ため
対象外。ダンプに `Completed` で現れるが本件とは無関係である。

## 3. 直し方

Kubernetes は Job の Pod にラベルを付ける。**「そのラベルを持たない」セレクタ**で generic に外す。

```sh
kubectl -n "$ns" wait --for=condition=Ready pods \
  -l '!job-name,!batch.kubernetes.io/job-name' --timeout=600s
```

- **新旧どちらのラベルも外す**（`batch.kubernetes.io/job-name` は新、`job-name` は互換で残る系）。
- 🔴 **フェイルセーフである。** ラベルがどの Pod にも無い環境では**「存在しない」条件が全 Pod に
  真**となり、**現状とまったく同じ対象集合**になる。**退行しない。**

### 採らなかった案

- `--for=condition=Available deployments --all` —— 正統な書き方だが、`platform-infra` の
  **DaemonSet（`inotify-sysctl`）が対象から漏れる**。射程を狭めたくないので採らない。
- `--field-selector=status.phase=Running` —— 待ち開始時に Running な Job Pod は含まれてしまい、
  その後 Completed になって同じ問題を起こす。**解決にならない。**
- Job 名の直指定（`-l 'app!=config-drift-postsync'`） —— 今後 Job が増えるたびに壊れる。

## 4. 検証

- `node scripts/check-workflow-*`（あれば）・文書系検査一式
- 🔴 **k8s の実走はできない**（後述）。**YAML の構文と、セレクタの意味論の裏取りに留まる。**

## 5. 🔴 実測できないこと

**この環境に Docker が無く、k3d クラスタを起こせない。** セレクタが意図どおり Job の Pod だけを
外すことは**実走で確かめていない**。根拠は Kubernetes のラベル付与の仕様と、
上記のフェイルセーフ性（ラベル不在なら現状と同一集合）である。

**「直った」と書けるのは Integration Stack が段 9 を越えてからである。**
段 9 は段 12（seed）・段 13（OIDC 検証）より手前なので、**本件が通るまで #1052 は評価されない。**
