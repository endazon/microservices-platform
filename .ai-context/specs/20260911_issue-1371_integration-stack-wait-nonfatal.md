---
title: integration-stack の Pod 待ちを非致命にし、readiness の判定を G1 に一本化する
issue: "#1371"
plan_refs:
  - NFR
adr_refs:
  - IADR-0232
status: done
created: 2026-09-11
---

# 作業仕様書: integration-stack の待ちステップを判定にしない（#1371）

## 起点

- issue #1371（自動起票）。`integration-stack` が develop の push で落ちた（run 34469913102 / 34484578461）。
- 落ちたステップは **「Wait for pods to become Ready（待つだけ。判定はしない）」**。`kubectl wait --timeout=600s` が
  16 Pod で期限切れになり、`set -euo pipefail` の下で非 0 → ジョブ失敗。

## 何が起きているか

このステップは #1055・#1316 で 2 度、「待ちの対象集合」の取り方を直してきた（Job の Pod を外す・削除中の Pod を外す）。
それでも run 34484578461 では `bff-service` の Pod が**新旧 2 つ**並んだまま期限を迎えた（待ち開始時点では削除中でなく、
その後の rollout で入れ替わる競合）。**待ち開始時の集合の固定**という `kubectl wait` の性質上、この形の競合は
対象集合の取り方をいくら精密にしても残る（同型 3 回目）。

一方、ステップ自身のコメントが「**ここは待ちであって判定ではない。判定は `check-stack-ready.js` の G1 が
fail-closed で行う**」と書いており、直後のステップが実際に門である。**待ちが落ちてジョブを止めるのは、
この分担と矛盾している。**

## 設計

`kubectl wait` の失敗を**警告にして続行**する。判定は従来どおり G1（readiness）が fail-closed で行う。

| 状況 | 変更前 | 変更後 |
| --- | --- | --- |
| 期限内に全 Pod が Ready | 続行 | 続行（不変） |
| 期限切れだが直後に Ready になる（rollout の競合） | **ジョブ失敗（偽陰性）** | 警告 → G1 が Ready を見て続行 |
| 本当に Ready にならない | ジョブ失敗 | 警告 → **G1 が fail-closed で落とす**（結論は同じ） |

**フェイルセーフの向きは変わらない**: 落とす責務が G1 へ一本化されるだけで、「Ready でないのに緑」は作れない。

### 採らなかった案

- **タイムアウトを延ばす**: 競合は時間ではなく集合の固定に由来する。延ばしても消えず、遅くなるだけ。
- **rollout が落ち着くまで待ってから wait を始める**: 「落ち着いた」の判定を別に要し、それ自体が G1 の仕事の複製になる。

## 走査した母集合（規則 2・9）

`Wait for pods to become Ready` / `condition=Ready` で `.github/workflows` と `scripts/*.test.js` を走査。
ワークフローは `integration-stack.yml` の 1 箇所。試験で固定しているものは無い（`k8s-local-up.test.js` の
`condition=Ready` は ExternalSecret の待ちで別物）。

## 受け入れ基準

- [x] 待ちの失敗でジョブが止まらない（`|| { warn; }` で続行）
- [x] 直後の門（`check-stack-ready.js` G1）は不変＝Ready でない Pod があれば落ちる
- [x] `node scripts/check-workflow-job-refs.js` / `check-action-versions.js` が緑
- [ ] develop の次回 `integration-stack` が緑（マージ後に確認）
