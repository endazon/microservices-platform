---
title: integration-stack でリセット申請の床を入れて T-10 の所要時間を測る（ADR-0094 フォローアップ 3）
issue: "#1410"
plan_refs:
  - SC-13
  - SC-15
  - ADR-0094
adr_refs:
  - IADR-0432
status: done
created: 2026-09-11
---

# 作業仕様書: CI で床を入れて測る（#1410 の後段）

## 起点

- PR #1417（IADR-0432）は床を opt-in（`RESET_FLOOR=1`）で入れ、integration-stack は床なしのため T-25（所要時間）が赤のまま。
  ADR-0094 フォローアップ 3 は「床を入れた構成での再実測を環流する」ことを求め、決定 4 は床が入るまでの赤を受け入れている。

## 設計

| 対象 | 変更 |
| --- | --- |
| `.github/workflows/integration-stack.yml` job env | `RESET_FLOOR: '1'`（`istio-edge-up.sh` が `deploy/local/edge-istio-reset-floor` を当てる。`ISTIO=''` のときは呼ばれないので無害） |

ローカルの既定（`k8s-local-up.sh`・`RESET_FLOOR` 未設定）は変えない。床の値（150 ms）は IADR-0432 の導出のまま。

## 受け入れ基準

- [x] `check-workflow-job-refs` ほか検査器が緑
- [ ] develop の integration-stack で T-25 が「反復中央値の比が自己対照内」で緑になる（結果を planning#596 / ADR-0094 へ環流）
