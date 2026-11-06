---
title: IADR-0687 BFF の LLM を伴う口に、利用者ごと・群ごとの直近 60 秒の要求数の上限（段 1 は報告、段 2 は 429）を共有の端点フィルタで掛ける（#2432）
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0032, ADR-0044]
author: claude
created: 2026-11-06
updated: 2026-11-06
related_specs:
  - ../specs/20261106_2432_bff-user-rate-limits.md
---

# IADR-0687: BFF の利用者ごとの要求数の上限（#2432）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-06
- 決定者: claude（#2432。コーディネーターの指示 2026-11-06）

## 起点・関連

- 起点 issue: #2432。
- 前提: 計画 ADR-0032（エッジは BFF）・ADR-0044（LLM 費用の計測）。
- 作業仕様書: `.ai-context/specs/20261106_2432_bff-user-rate-limits.md`。

## 決定

### 決定 1 — 共有の端点フィルタ `UserRateGate.LimitPerUserOrReport(群, 既定)` を、LLM を伴う 3 つの口に掛ける

| 群 | 口 | 既定（件/直近 60 秒/利用者） |
| --- | --- | --- |
| `analysis` | `/bff/analysis/*` | 20 |
| `search` | `/bff/search` | 60 |
| `graph-suggestion` | `/bff/graph/suggestions/generate/{id}` | 10 |

- 利用者は検証済みの主体（`sub` → `NameIdentifier` → `Name`）。未認証は群の門が先に落とすので、ここでは数えない。
- 上限は `Bff:UserRateLimits:Groups:<群>` で変える。［2026-11-06 / AI レビュー］設定（段・群の上限）は**起動時に**検証し、不正なら起動を止める（`AddUserRateLimits` の ValidateOnStart。BFF の Program.cs が呼ぶ）。不正な値は、段の綴り違い・数値でない値・0 以下・未知の群（`UserRateGate.KnownGroups` に無いキー）である。
- 計器 `bff.user_rate.exceeded.total` の属性は、群と段だけにする。利用者の識別子は計器にもログにも載せない（基数と個人情報の両方の理由）。

### 決定 2 — 段は `Bff:UserRateLimits:Mode`。既定は `Report`

- `Report`（段 1）: 超えた要求を数えるだけで、後段へ転送する。
- `Enforce`（段 2）: 429 に `Retry-After: 60` を付けて返し、後段へ転送しない。
- ［2026-11-06 / AI レビュー］**超えた要求は、段に依らず記録しない。** 利用者ごとの記録は上限の件数で頭打ちになる（段 1 でも際限なく伸びない）。「どの要求が超えたか」の判定は段 1 と段 2 で同じなので、段 1 の計器は段 2 で拒む件数をそのまま表す。

## 結果・残余リスク

1. 状態はレプリカごとにプロセス内で持つ。BFF は 2 レプリカ以上で動くので、実効の上限はレプリカ数倍になる。共有の置き場（Valkey 等）へ移すのは別の判断として扱う。
2. 既定の値は技術的な上限であり、計画の値ではない。段 1 の計器で、実際の利用が上限に掛からないことを確かめてから段 2 へ切り替える。
3. エッジ（Ingress）のレート制限は扱わない。
