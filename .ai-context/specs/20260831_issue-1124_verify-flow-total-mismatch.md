---
title: integration-stack の段数宣言（TOTAL）が実行段数と 1 ずれている
type: spec
status: done
related_ids: [NFR, FR-03, SC-01, ADR-0006, IADR-0313, IADR-0318]
author: Claude (orchestrating session)
created: 2026-08-31
updated: 2026-08-31
---

# 仕様書: issue #1124 — `verify-oidc-edge-flow.sh` の TOTAL が 1 少ない

## 症状

`develop` の `integration-stack` が落ちている。**落ちているのは 1 件だけ**である。

```
FAIL  実行した段が 23 本で、宣言（TOTAL=22）と一致しない
結果: PASS 30 / FAIL 1（段 23/22）
導線に失敗があります。
```

**30 件の実質的な判定はすべて PASS しており、失敗しているのは自己整合チェックだけ**である。
つまり**導線そのものは通っているのに、門が「宣言と実行が食い違う」と言って赤にしている。**

## 原因（実測）

`TOTAL` はモードごとの加算式で持つ単一情報源である。

```
TOTAL=11
if [ "$ABAC_POSITIVE" = "1" ]; then TOTAL=$((TOTAL + 6)); fi
if [ "$SEARCH_SEEDED" = "1" ]; then TOTAL=$((TOTAL + 2)); fi
if [ "$SEARCH_HITS"   = "1" ]; then TOTAL=$((TOTAL + 3)); fi   # ← ここが 1 少ない
```

ブロックごとの段数を機械的に数えると:

```
$ awk '/^if \[ "\$SEARCH_HITS" = "1" \]; then$/,/^fi$/' scripts/verify-oidc-edge-flow.sh | grep -c "next_step "
4
$ awk '/^if \[ "\$SEARCH_SEEDED" = "1" \]; then$/,/^fi$/' scripts/verify-oidc-edge-flow.sh | grep -c "next_step "
2
$ awk '/^if \[ "\$ABAC_POSITIVE" = "1" \]; then$/,/^fi$/' scripts/verify-oidc-edge-flow.sh | grep -c 'step "'
6
```

**`SEARCH_HITS` ブロックは 4 段あるのに増分が 3 である。**

🔴 **書き手が「足した段」を数えて「そのブロックの段数全部」を書かなかった。**
`#1117`（`IADR-0318`）が 3 段を足したとき、**既に在った 1 段（合言葉のヒット。`#992` / `IADR-0313` 由来）を数え落として**
`+1` → `+3` にした。正しくは `+1` → `+4` である。

`git rev-parse --is-shallow-repository` = `false`（履歴は完全。出典に使える）。

## 変更

増分を `+4` へ直し、**内訳のコメントを「足した段」ではなく「そのブロックの段数全部」と書き換えた**（同じ読み違いを次に起こさせないため）。実測の仕方（`awk` で範囲を切って `next_step` を数える）もコメントに残した。

## 検査器を足すか（`.claude/rules`「同型の事故が 2 回起きたら」）

**足さない。1 回目である。**

`git log -S'TOTAL=$((TOTAL' -- scripts/verify-oidc-edge-flow.sh` の結果は `dcdba39c`（`#1021`。加算式そのものを導入したコミット）**ただ 1 件**で、
**増分の数え違いが起きたのは今回が初めて**である。

なお **STEPS / TOTAL の突合そのものが既にこの種の検査器**であり、実際に事故を捕まえている。
不足しているのは「PR の時点で捕まえられない（後段の `integration-stack` まで分からない）」点だけである。

🔴 **2 回目が起きたら置くもの**: 上の `awk` と同じ数え方でブロックごとの段数を数え、増分と突き合わせる静的検査
（`scripts/` へ置き、`ci.yml` の `static-checks` へ配線する）。**列挙を持たず走査で数えること**、
**走査 0 件は fail-closed にすること**（`scripts/check-collector-self-telemetry.js` が同じ作法を採っている）。

## 検証

- `bash -n scripts/verify-oidc-edge-flow.sh` → 構文 OK
- ブロックごとの段数（4 / 2 / 6）と増分（+4 / +2 / +6）が一致することを上の `awk` で実測
- 判定は **CI の `integration-stack` 実走**で行う（ローカルには稼働スタックの全経路が無い）

## 測れなかったもの

**ローカルでの `verify-oidc-edge-flow.sh` 通し実行**。`developer` は TOTP 登録済みで段 4 が `OIDC_TOTP_SECRET` を要求して止まる（`#1114` が実測済み）。本変更は段の数え方だけで導線の挙動を変えないため、CI に委ねた。
