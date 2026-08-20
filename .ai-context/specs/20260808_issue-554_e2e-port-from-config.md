---
title: 作業仕様書 分割スモークの接続先を設定から得て、空振りが緑にならないようにする
type: spec
status: done
related_ids: [NFR, ADR-0031, IADR-0134]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs: []
related_specs:
  - ../adr/IADR-0134_spa-route-code-splitting-boundaries.md
  - ../../docs/how-to/session-handoff.md
---

# 仕様書: 分割スモークの接続先を設定から得て、空振りが緑にならないようにする

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#554**（親 #454）／起点 ID: **NFR**
- 制約: **ADR-0031** ／ [IADR-0134](../adr/IADR-0134_spa-route-code-splitting-boundaries.md)（分割境界。本テストはその回帰ガードの一部）
- 対象: [`src/platform/frontend/e2e/bundle-splitting.smoke.spec.ts`](../../src/platform/frontend/e2e/bundle-splitting.smoke.spec.ts)
  ／ [`src/platform/frontend/playwright.config.ts`](../../src/platform/frontend/playwright.config.ts)
- 分類（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4 ＝ **監査強度**の分岐）: **機械検査を新設・改修する**
  （E2E の回帰ガードを改修し、同行が要求する**変異試験**を実施した）。
  したがってフェーズ末クロス監査は**全面 1 巡 ＋ 是正差分 1 巡**が要る。
  なお同決定 4 は**監査の巡数を決める表であり、IADR を書くべきかの分類ではない** ——
  後者は下記「IADR を作らない理由」で別に論じる

## 目的

`bundle-splitting.smoke.spec.ts` の 4xx/5xx 検査が、**接続先がずれると「何も見ずに緑」になる**構造を塞ぐ。

## 母集合の引き直し（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）

**走査基準**: `origin/develop` = `96c2dbe`。

issue 本文は「同じ値が `playwright.config.ts` にも 3 箇所」と述べる。**これを転記せず引き直した。**

```console
$ grep -rn "4173" src/platform/frontend/ --include=*.ts --include=*.json --include=*.js
```

| 箇所 | 実測 |
| --- | --- |
| `e2e/bundle-splitting.smoke.spec.ts` | 1（フィルタの直書き） |
| `playwright.config.ts` | 3（`use.baseURL` / `webServer.command` の `--port` / `webServer.url`） |
| **合計** | **4**（issue 本文の記述と一致） |

**他の e2e 12 本は `page.goto('/')` の相対指定のみで、ポートを直書きしていない**（同走査で 0 件）。
したがって是正対象は上記 2 ファイルに閉じる。

**引かなかった軸と理由**: `vite.config.ts` の `preview.port` は**設定していない**（既定 4173 に依存）。
`--port` を明示で渡しているため実害は無く、本 issue の射程（テストが設定を参照しない問題）の外である。

## 変異試験（是正の前後で実測）

**issue の要求「是正前に『空振りしても通る』ことを実測してから直す」に従った。**

| # | 変異 | 是正**前** | 是正**後** |
| --- | --- | --- | --- |
| **M1** | フィルタの origin を `:9999` へずらす | **①（4xx 検査）は通り、②（チャンク数）だけが落ちた**（`Received: 0`） | — |
| **M1b** | M1 に加え②を無効化（`toBeGreaterThan(-1)`） | **緑（1 passed）** ——1 件も観測せずに合格 | — |
| **M2** | フィルタの origin を `:9999` へずらす | （M1 と同じ） | **落ちる**: `http://localhost:9999 宛の応答を 1 件も観測していません` |
| **M3** | `use.baseURL` を削除 | 直書きのため**影響なし＝緑** | **落ちる**: `playwright.config.ts の baseURL が未設定です` |
| **M4** | 4173 を無関係なプロセスが占有（`reuseExistingServer: true`） | **無関係なサーバーへ接続**し、5.1 秒後に `element(s) not found` で落ちる（原因が判らない） | — |
| **M5** | 同上（`reuseExistingServer: false`） | — | **即座に落ちる**: `http://localhost:4173 is already used, ...` |

**M1b が本 issue の核の実証である** —— ポートがずれた状態で 4xx 検査が**緑のまま通った**。

## やったこと

1. **`playwright.config.ts` の値の複写を無くした。** `PREVIEW_PORT` / `PREVIEW_URL` の 2 定数から
   `baseURL` / `webServer.command` / `webServer.url` の 3 箇所を導く。
2. **テストが設定から接続先を得るようにした。** `baseURL` フィクスチャを受け取り、
   未設定なら**先頭で落とす**（M3）。
3. **空振り検出を足した。** `observed` に観測した応答を積み、**0 件なら 4xx 検査より先に落とす**（M2）。
   「捨てた結果の空配列」と「本当に問題が無い空配列」を区別できないのが事故の核だった。
4. **`reuseExistingServer: false` に決めた**（下記）。

## `reuseExistingServer` の方針（issue の受け入れ基準）

**ローカルでも常に自前で起動する**（既定の `!process.env.CI` を採らない）。

- **理由**: `--strictPort` は「このポートを占有できなければ失敗する」という宣言である。再利用を許すと
  **同じポートに居る無関係なプロセスを本アプリだと思って接続する**（M4 で実測。5.1 秒かけて
  `element(s) not found` という**原因の判らない失敗**になる）。常に自前起動なら M5 のとおり
  **占有時点で `already used` と即座に落ちる**。
- **本 issue が正そうとしている「空振りが緑になる」の逆側**（誤った対象を見て落ちる）であり、同じ理由で塞ぐ。
- **代償**: ローカルで毎回プレビューを起動する待ち時間。**実測 1 秒程度**（`webServer.timeout` は 60 秒のまま）。
  取り違えの危険に見合わない。
- **CI では挙動が変わらない**（従来も `process.env.CI` により `false` だった）。**変わるのはローカルだけ**である。

## IADR を作らない理由

根拠は `CLAUDE.md`「重要な実装判断（内部設計・ライブラリ選定等）は実装 ADR に必ず残す」の**適用範囲**である。
本 PR は [IADR-0134](../adr/IADR-0134_spa-route-code-splitting-boundaries.md) が定めた分割境界を**変えず**、その回帰ガードが空振りしないよう直すだけで、
**他の実装が従うべき決定を 1 つも作らない**。`reuseExistingServer` の方針は
**このテストハーネスに閉じた設定**であり、他の実装が従うべき決定ではない
（issue の受け入れ基準も「方針が**記録**されている」であり、IADR を求めていない）。
記録先は本書と `playwright.config.ts` のコメントの 2 箇所で、**理由は後者に置き前者から参照する**
（同じ理由文を 2 箇所に持たない。§5 型 3）。

## 受け入れ基準の充足

| issue の基準 | 結果 |
| --- | --- |
| テストがポートを直書きしていない | ✅ `baseURL` フィクスチャから得る |
| **フィルタが 0 件になったら落ちる** | ✅ M2 で実測 |
| 上記の変異試験で是正前後の差が実測されている | ✅ 上表 M1 / M1b / M2 / M3 |
| `reuseExistingServer` の方針が記録されている | ✅ 本節 ＋ `playwright.config.ts` のコメント |
| E2E が green | ✅ **13 passed**（全スモーク） |

## 検証（実走した結果）

| コマンド | 結果 |
| --- | --- |
| `pnpm run test:e2e` | **13 passed** |
| `pnpm run lint` | **0 errors**（warning 9 件は既存・本 PR と無関係） |
| `pnpm run typecheck` | **4 プロジェクトすべて Done** |

> **この環境では E2E が実走できる。** 引継資料 §4.5 は「実走できない」と書いているが、
> `src/ai-stock-trading` submodule を populate すれば `build` も E2E も通る（本 PR で実測）。
> §4.5 の是正は別 issue で扱う。
