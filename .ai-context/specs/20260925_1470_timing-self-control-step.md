---
title: 作業仕様書 — T-10 の所要時間判定に段 1（中央値の差が自己対照の刻み以下なら合格）を足す（#1470・計画 ADR-0103）
type: spec
status: done
related_ids: [SC-15, FR-05, NFR-13, ADR-0094, ADR-0097, ADR-0103, IADR-0432]
author: claude
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0103_degenerate-self-control-is-not-a-bound.md (Accepted 2026-09-15)
  - planning:projects/microservices-platform/07_adr/ADR-0094_existence-hiding-timing-median-consistency-and-response-floor.md
  - planning:projects/microservices-platform/07_adr/ADR-0097_timing-floor-release-default-on-and-periodic-review.md
related_specs: [20260911_issue-1410_reset-timing-floor, 20260925_1487_plan-adr-range-0105]
issue: "#1470"
---

# 作業仕様書 — T-10 の所要時間判定に段 1 を足す

## 起点

- issue: #1470（床の内側 1 ms の揺れで T-10 の所要時間判定が不合格になる）／ 環流 planning#633 → 裁定 planning#634
- 計画 ADR-0103（Accepted 2026-09-15）決定 1〜4・§結果 フォローアップ 1
- 前提: #1487（計画 ADR レンジを `0001..0105` へ。本作業のコミット件名が `ADR-0103` を引くため）

## 計画の決定（逐語の要点）

| 決定 | 内容 | 本作業での写像 |
| --- | --- | --- |
| 1 | **判定を 2 段にする。** 段 1: 実在・非実在の中央値の差が**自己対照の刻み以下** → **合格**（比と自己対照を比べない）。段 2: 差が刻みを超える → 従前どおり比が自己対照を超えないこと | `evaluateTimingConsistency` に段 1 を足す |
| 1 | **刻みの値は計画が発明しない。自己対照の中央値が取り得る最小の間隔から導く。整数 ms の標本では 1 ms。導出は実装（IADR-0432 の後継）へ残す** | 刻みを標本の分解能（`Date.now()` の差＝整数 ms）と自己対照の各群の標本数から**計算する**。IADR-0432 へ追記 |
| 2 | 「自己対照が広いとき」の `評価不能` は改めない。**広すぎる側を緩めない** | 段 1 に当たっても `self >= SELF_CONTROL_WIDE_RATIO` なら `評価不能`（下記「決定 1 と 2 の交点」） |
| 3 | 1 ms 未満の系統差を見逃すリスクを受け入れる | 受け入れたリスクとして IADR-0432 追記に書く |
| 4 | 床（150 ms）は引き直さない | 床の値・置き場に触れない。ADR-0097 決定 3 の契機「検査器の赤」が発火し、床超過ではなかったことを IADR-0432 追記に残す（導出を引き直していないことも） |
| FU 1 | **判定順は段 1 を `cross > self` より前に置く** | 同左 |

### 決定 1 と 2 の交点（実装の解釈）

決定 1 は段 1 を「合格」とし、決定 2 は「広すぎる側を緩めない」とする。**段 1 を無条件に合格にすると、
自己対照が 2 倍以上に広い（ノイズに埋もれた）反復でも中央値が偶然 1 ms 以内に並べば合格になり、
広すぎる側を緩めることになる。** よって段 1 は**潰れた側（自己対照が機能しない側）にだけ効かせ**、
自己対照が広い反復は段 1 に当たっても `評価不能` とする。ADR-0103 §決定 2 の「潰れた側に別の扱いを
与えるだけであり、広すぎる側を緩めない」の字面どおりである。

### 刻みの導出

- 標本の分解能 `TIMING_SAMPLE_RESOLUTION_MS = 1`（`submitReset` が `Date.now()` の差で測るため整数 ms）。
- 中央値の刻み: 群の標本数が**奇数なら観測値そのもの＝分解能**、**偶数なら中央 2 値の平均＝分解能 / 2**。
- 自己対照の刻み = 2 群それぞれの中央値の刻みのうち**細かい方**（2 つの中央値の差が表せる最小の間隔）。
- 現行の構成（片側 6 標本 → 自己対照の各群 3 標本・奇数）では **1 ms**。計画の「整数 ms の標本では 1 ms」と一致する。

## 母集合（着手時に引き直した）

**軸 1（判定を説明する語）**: `git grep -l -E "自己対照|evaluateTimingConsistency|SELF_CONTROL|cross > self|比 > 自己|許容比"`

| ファイル | 扱い |
| --- | --- |
| `scripts/check-password-reset-mail.js` | **対象**（冒頭の説明・関数の説明・判定・自己試験） |
| `.ai-context/adr/IADR-0432_…` | **対象**（日付付き追記） |
| `.ai-context/adr/README.md`（IADR-0432 の行） | 検討したが**変えない**。索引のタイトルセルは本体 `title:` の要約に限られ、日付付き追記を書くと `scripts.repo.test.js` の索引検査が `title-addendum` / `title-too-long` で落とす（実測）。本体 `title:` は変わらないので行も変わらない |
| `docs/tests/SC-15_password-reset.md`（T-25 の行） | **対象**（期待結果が 2 段になる） |
| `docs/screens/SC-15_password-reset.md`（判定条件の段落） | **対象** |
| `docs/screens/SC-13_login.md:155` / `docs/tests/SC-13_login.md:145` | 除外。ログイン経路が判定対象外である理由（反復を前提とする）の説明で、段 1 はその理由を変えない |
| `.ai-context/specs/20260911_issue-1410_*` | 除外。確定済みの作業仕様書（凍結） |
| `CHANGELOG.md` | 除外。生成物 |
| `docs/how-to/plan-id-range-history-annex.md` | 除外。レンジの記録（#1487 で書いた計画 ADR の要約のみ） |

**軸 2（検査器の名前）**: `git grep -l check-password-reset-mail -- . ':!src/ai-stock-trading'` —— 31 件
（本仕様書のコミット前。本仕様書自身がこの語を含むため、コミット後の同じ走査は 32 件を返す）。判定式を述べるのは軸 1 と同じ 5 件だけ。
他は送出・本文・門・realm の説明であり所要時間の判定式を述べない。`scripts/README.md:44` の行は所要時間の軸に
触れていない（除外）。`check-login-existence-disclosure.js` は本スクリプトから関数を借りるが
`evaluateTimingConsistency` は借りない（`require` の分割代入を確認）。

**軸 3（テスト ID `T-25` / `T-10`）**: MSP のテスト仕様書 `docs/tests/SC-15` では所要時間の項目が **T-25**、
検査器の出力札は **`[T-10][所要時間]`** である（#1417 以来の不一致）。計画 ADR-0103 §実測 5 は「`T-25` は一度も
存在していない」と書くが、根拠の走査は検査器ファイルに限られており、**テスト仕様書には T-25 がある**。
本作業は ID を付け替えない（射程外）。報告と、必要なら計画への環流で扱う。
`.github/workflows/integration-stack.yml:236` のコメント「所要時間（T-25）はここで赤になる」も同じ ID を使うため除外（判定式を述べていない）。

**新しいテスト ID は採番しない。** 段 1 は T-25 の受け入れ基準（所要時間も区別できない）の判定式の改定であり、
行を分けると同じ基準が 2 行に割れる。検査器の自己試験（T-19 の陽性対照の系）へケースを足す。

## 受け入れ基準

- [x] 段 1: 自己対照 1.00・中央値 153.0 / 152.0 ms（#1470 の実測の形）の反復が**合格**（従前は不合格）
- [x] 段 1: 差 0.5 ms（153.0 / 152.5）も合格
- [x] 段 2: 差が刻みを超えれば従前どおり（床なしの形は不合格・差 2 ms で自己対照 1.00 なら不合格）
- [x] 決定 2: 自己対照が広い反復は、中央値の差が刻み以下でも `評価不能`（緑にしない）
- [x] 刻みの導出: 奇数個の群は分解能、偶数個の群を含めば分解能 / 2
- [x] 出力に刻みと段を併記する（どちらの段で合格したかが読める）
- [x] `node scripts/check-password-reset-mail.js --self-test` が緑
- [x] IADR-0432 に `［2026-09-25 追記 / #1470］`（索引の行は上の理由で据え置き）

## 触らないもの

- 床の値（150 ms）・置き場・opt-in / 既定 ON（ADR-0097 決定 2 の配備は別作業）
- `SELF_CONTROL_WIDE_RATIO` の値（ADR-0103 決定 2）
- 反復数・標本数（ADR-0103 は案 3「標本数を増やす」を採っていない）
