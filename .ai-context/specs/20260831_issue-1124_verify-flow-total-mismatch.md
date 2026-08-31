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

**新しい検査器は足さない。1 回目である。**
`git log -S'TOTAL=$((TOTAL' -- scripts/verify-oidc-edge-flow.sh` の結果は `dcdba39c`（`#1021`。加算式そのものを導入したコミット）**ただ 1 件**で、増分の数え違いが起きたのは今回が初めてである。

🔴 **ただし、既に在る検査が「検査になっていなかった」ことが分かったので、そちらは直す。**

`scripts/scripts.repo.test.js` には加算値を照合するテストが既に在った。**しかし加算値をテスト側へ書き写して比べていた**:

```js
for (const [flag, addend] of [['ABAC_POSITIVE', 6], ['SEARCH_SEEDED', 2], ['SEARCH_HITS', 3]]) {
```

**写しなので、書き手が両方を同じ誤った値で揃えると検出力がゼロになる。**
実際 `#1117` は増分と**このテストの期待値の両方**を `3` に揃えており、**テストは緑のまま通った。**
後段の `integration-stack` で「実行 23 対宣言 22」として初めて発覚している。

→ **加算値をスクリプトから導出する形へ直した**（ブロックを切って `next_step` / `step "` を数える）。
**これは検査器の新設ではなく、既存の検査が写しになっていたことの是正である。**

### 変異試験（導出が効くことの証跡）

```
# 増分を +4 → +3 へ戻す
AssertionError: SEARCH_HITS の加算がブロックの実段数（4）と違う（…）

# ABAC_POSITIVE の増分を +6 → +5 へ
AssertionError: ABAC_POSITIVE の加算がブロックの実段数（6）と違う（…）

# 両方を戻す
✓ 668 tests passed
```

**両方向で落ちることを確かめた。** 走査が空振りしたときのために「ブロックに段が 1 つも無い」を fail にしてある（0 件を緑と読ませない）。

## 検証

- `bash -n scripts/verify-oidc-edge-flow.sh` → 構文 OK
- ブロックごとの段数（4 / 2 / 6）と増分（+4 / +2 / +6）が一致することを上の `awk` で実測
- 判定は **CI の `integration-stack` 実走**で行う（ローカルには稼働スタックの全経路が無い）

## 測れなかったもの

**ローカルでの `verify-oidc-edge-flow.sh` 通し実行**。`developer` は TOTP 登録済みで段 4 が `OIDC_TOTP_SECRET` を要求して止まる（`#1114` が実測済み）。本変更は段の数え方だけで導線の挙動を変えないため、CI に委ねた。
