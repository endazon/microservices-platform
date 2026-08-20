---
title: 作業仕様書 manualChunks の規則構成を守る機械検査を新設し、CI へ結線する
type: spec
status: done
related_ids: [NFR, ADR-0031, IADR-0134, IADR-0147]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs: []
related_specs:
  - ../adr/IADR-0147_chunk-rule-presence-check.md
  - ../adr/IADR-0134_spa-route-code-splitting-boundaries.md
---

# 仕様書: manualChunks の規則構成を守る機械検査を新設し、CI へ結線する

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#556**（親 #454）／起点 ID: **NFR**
- 制約: **ADR-0031** ／ [IADR-0134](../adr/IADR-0134_spa-route-code-splitting-boundaries.md) 決定 3（`manualChunks` の 3 規則）
- 新設した実装 ADR: [IADR-0147](../adr/IADR-0147_chunk-rule-presence-check.md)
- 分類（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4 ＝ **監査強度**の分岐）: **機械検査を新設・改修する**
  → フェーズ末クロス監査は**全面 1 巡 ＋ 是正差分 1 巡**（変異試験の妥当性を必ず見る）

## 母集合の引き直し（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）

**走査基準**: `develop` = `96c2dbe`。**issue 本文の数値を転記せず、実ビルドから引き直した。**

| 項目 | issue 本文の記述 | **実測** |
| --- | --- | --- |
| 初期ロード合計 | 577.54 kB | **577.68 kB** |
| 1 kB 未満の遅延チャンク | 3 本 | **5 本** |
| `manualChunks` の規則 | 3 | 3（`ui` / `vendor-react` / `vendor-query`。一致） |

**2 件が古かった。** #512 以降に `orvalSelect` 等のチャンクが増えているためである。
**床は実測値で作った。**

### 引かなかった軸と理由

| 軸 | 引いたか | 理由 |
| --- | --- | --- |
| `dist` が存在する CI ジョブ | ✅ | 結線先の決定に要る。**`frontend.yml` の `build-test` だけ**（`ci.yml` の `scripts-tests` には無い） |
| gzip 後のサイズ | ❌ | 床としては圧縮実装の版で動き不安定（[IADR-0147](../adr/IADR-0147_chunk-rule-presence-check.md) 検出しないこと） |
| チャンクの中身（モジュール帰属） | ❌ | 規則の「書き換え」は射程外。名前の実在と量の 2 面で見る |

## ★ 変異試験 —— **issue の設計案が捕まえられないことを実測した**

issue は判定を 3 種（①500 kB 超 fail／②初期ロードのラチェット fail／③1 kB 未満の本数 warn）と定めていたが、
**この 3 種では受け入れ基準「規則を 1 つ外すと検査が落ちる」を満たせない。**

### 実ビルドでの測定（fixture ではなく `pnpm run build` の出力）

| 変異 | 最大チャンク | 初期ロード合計 | 1 kB 未満 | 当該チャンク |
| --- | --- | --- | --- | --- |
| `ui` 規則を外す | 306.69 kB（**< 500**） | **544.87 kB（減る）** | 5 → 9 | **消える** |
| `vendor-react` 規則を外す | 458.92 kB（**< 500**） | 578.55 kB（**+0.15%**） | 5 → 5（**不変**） | **消える** |

- **① はどちらでも発火しない。**
- **② は `ui` では発火しない**（規則を外すと初期ロードが**減る**ため。向きが違う）。
- **③ は `vendor-react` では 1 本も動かない。**

→ **判定 1「必須チャンクの実在」を新設**し、最優先の fail に置いた（[IADR-0147](../adr/IADR-0147_chunk-rule-presence-check.md) 決定 1）。

### 是正後の実ビルド検証（**検査器を実際に当てた**）

| 変異 | `check-chunk-budget.js --require` の結果 |
| --- | --- |
| `ui` 規則を外す | **exit 1** — `必須チャンク "ui" が成果物に存在しない` ＋ 1 kB 未満の増加を warn |
| `vendor-react` 規則を外す | **exit 1** — `必須チャンク "vendor-react" が成果物に存在しない` ＋ 床超過 |
| 規則を戻す | **exit 0** — `OK: 必須チャンク 3 本すべて実在 …` |

## やったこと

1. **`scripts/check-chunk-budget.js` を新設**（判定 4 種 ＋ `--require` / `--update` / `--self-test`）。
2. **`scripts/chunk-budget-baseline.json` を新設**（実測値。`requiredChunks` / `maxChunkBytes` /
   `initialTotalBytes` / `smallLazyChunks`）。
3. **`scripts/scripts.repo.test.js` へ self-test を登録**（`scripts.test.js` は変更禁止・[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md) 分類 A）。
   件数だけでなく **`変異 M6` / `変異 M7` が実際に走っていること**を照合する
   （件数照合だけだと変異ケースを消しても通り続ける）。
4. **`.github/workflows/frontend.yml` の `build-test` へ結線**（`Build` の直後・`--require`）。
5. [IADR-0147](../adr/IADR-0147_chunk-rule-presence-check.md) を新設し、索引へ 1 行足した。

## self-test が自分の欠陥を捕まえた記録

`baseline.requiredChunks` と `vite.config.ts` の突き合わせ（[IADR-0147](../adr/IADR-0147_chunk-rule-presence-check.md) 決定 5）が、
**最初の実装の取りこぼしを検出した** —— 抽出が `return '<name>'` しか見ておらず、
三項演算子で返している `ui`（`return id.includes('/packages/ui/') ? 'ui' : undefined`）が漏れていた。
**この検査が無ければ `ui` を床から落としたまま緑で通っていた。**

## 受け入れ基準の充足

| issue の基準 | 結果 |
| --- | --- |
| `manualChunks` の規則を 1 つ外すと検査が落ちる（2 規則それぞれで実測） | ✅ 上表「是正後の実ビルド検証」。**issue の 3 判定では満たせないことも実測**し、判定 1 を新設した |
| 正常な構成では通る | ✅ `exit 0` |
| `--self-test` が通り、`scripts.test.js` から実行される | ✅ **12 件通過**／`scripts.test.js` は **293 passed**（+1） |
| **CI で実際に走る**（結線済み） | ✅ `frontend.yml` の `build-test` へ結線。**当該ステップが実行されていること**を PR の CI ログで確認する |

## 検証（実走した結果）

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-chunk-budget.js --self-test` | **12 件すべて通過** |
| `node scripts/check-chunk-budget.js --require`（実 dist） | **OK**（初期 577.68 kB / 最大 274.46 kB / 1 kB 未満 5 本） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **293 passed** |
| `node scripts/check-adr-numbering.js` | OK |
| `node scripts/check-doc-links.js` | OK |

## 申し送り

- **`.github/workflows/` を編集できたのは実行環境の差である。** #556 が「AI だけでは完結しない」と
  されていた根拠は環境固有のものであり、本セッションのローカル認証は `workflow` スコープを持つ。
  引継資料 §4.5 の是正は **#617** で扱う。
