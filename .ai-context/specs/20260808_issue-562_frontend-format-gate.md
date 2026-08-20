---
title: 作業仕様書 フロントエンドに format ゲートを新設し、CI へ結線する
type: spec
status: done
related_ids: [NFR, ADR-0031]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs: []
related_specs:
  - ../../docs/how-to/session-handoff.md
---

# 仕様書: フロントエンドに format ゲートを新設し、CI へ結線する

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#562**（親 #454）／起点 ID: **NFR**
- 契機: **PR #559（#519）の AI レビュー 🟢**（テスト末尾の余分な空行）
- 対照: `.github/workflows/ci.yml` の `dotnet format --verify-no-changes`
- 分類（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4 ＝ **監査強度**の分岐）: **機械検査を新設・改修する**
  → フェーズ末クロス監査は**全面 1 巡 ＋ 是正差分 1 巡**

## 目的

バックエンドにはある整形ゲートがフロントには無く、整形揺れを**人の目でしか拾えない**非対称を解消する。

## IADR を作らない理由

`CLAUDE.md`「重要な実装判断は実装 ADR に必ず残す」の適用範囲外と判断した。本 PR が決めたのは
**既存スタックへ標準的なツール（prettier。既に devDependency に在る）を通常の用法で結線すること**であり、
新しい設計上の制約を作らない。除外範囲の判断根拠は `.prettierignore` に理由つきで書いてあり、
そこが単一情報源になる（ADR へ転記すると二重に持つことになる）。

## ★ 母集合の引き直し（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）—— **ここが本 issue の実質**

**走査基準**: `develop` = `96c2dbe`。

issue は「フロントに format ゲートが無い」としか書いていない。**対象範囲は自分で引いた。**
素朴に `src/` 全体を対象にすると**まったく違うものが入る**。

| 走査 | 該当 | 判断 |
| --- | ---: | --- |
| `src/` 全体（`ts,tsx,js,cjs,mjs,json,css,md`） | **819** | ✗ 広すぎる |
| └ うち **.NET のビルド成果物**（`bin/` `obj/` `appsettings.json`） | **750** | **除外** —— C# 側は `dotnet format` が持つ |
| `ai-stock-trading/frontend`（**別リポジトリの submodule**） | 8 | **除外** —— 本リポから変更しない |
| `foundation/api/generated/`（orval 生成物） | **28** | **除外** —— 整形すると CI の再生成差分検査が**必ず落ちる** |
| `foundation/i18n/locales/`（Lingui カタログ） | 2 | **除外** —— 同上 |
| `*.md` | 2 | **除外** —— 日本語の散文・表を再流し込みする。本 issue の契機はコードの揺れ |
| **最終的な対象** | **68** | platform 23 / knowledge 37 / packages 5 / ルート設定 3 |

> **除外は「手で書かないもの」と「本リポジトリが所有しないもの」に限った。**
> 「整形すると落ちるから」を理由に手書きコードを外していくと、除外リストそのものが
> ゲートの無効化装置になる（`.prettierignore` の冒頭に同じ注意を書いた）。

### 引かなかった軸と理由

| 軸 | 引いたか | 理由 |
| --- | --- | --- |
| `packages/ui`（Storybook 含む） | ✅ **対象に含めた**（issue が「含めるかを決めよ」と指定） |
| `.json` | ✅ 対象（`tsconfig.json` 1 件のみ該当） |
| `pnpm-lock.yaml` | ❌ 生成物 |
| バックエンドの `.cs` | ❌ 既存ゲートあり。**二重に持たない** |

## 整形規約

`src/.prettierrc.json` —— **既存コードの実態に合わせた**（新しい流儀を持ち込まない）。

| 設定 | 値 | 根拠 |
| --- | --- | --- |
| `singleQuote` | `true` | 既存コードがシングルクォート |
| `printWidth` | `100` | 既存の行長 |
| `trailingComma` | `all` | 既存コード |
| `semi` / `arrowParens` | `true` / `always` | 既存コード（prettier の既定と同じ） |

**対象範囲の単一情報源は `.prettierignore` ただ 1 つ**とし、`package.json` のスクリプトは
`prettier --write .` / `prettier --check .` に留めた。**グロブを 2 本のスクリプトへ複写しない**
（複写は必ずずれる。#554 が直したのと同じ型）。

## 変異試験（**ゲートになっていることの実測**）

| # | 変異 | `pnpm run format:check` |
| --- | --- | --- |
| — | baseline（整形適用後） | **exit 0** |
| **F1** | テスト末尾に余分な空行（**#562 の契機と同型**） | **exit 1** |
| **F2** | import のクォートをシングル → ダブル（1 箇所） | **exit 1** |
| **F3** | インデントを 2 → 6 スペース | **exit 1** |
| — | 復帰 | **exit 0** |

**「入れて落ちなければゲートになっていない」**（issue の受け入れ観点）を 3 型で満たした。

## 一括適用が挙動を変えていないことの検証

整形は 68 ファイルに及ぶため、**壊していないことを実測した**。

| 検証 | 結果 |
| --- | --- |
| `pnpm run typecheck` | 4 プロジェクトすべて Done |
| `pnpm run lint` | **0 errors**（warning 9 件は既存・整形と無関係） |
| `pnpm run test` | 61 files / **576 tests passed** |
| `pnpm run codegen` → 生成物の差分 | **差分なし**（生成物を除外できている実証） |
| `pnpm run i18n` → カタログの差分 | **差分なし** ／ `check-i18n-catalogs` OK |
| `src/ai-stock-trading` の作業ツリー | **無変更**（submodule を汚していない） |

> **codegen の検証は 1 度空振りさせている。** `src/` の中から
> `git diff -- src/platform/...` を実行してしまい、**パスが 1 件も一致しないまま exit 0** で
> 「差分なし」と読んでいた。リポジトリルートから引き直して確認し直した。
> **本セッションが #554 で直したのと同じ「空振りが緑になる」型を、検証手順の側で踏んだ。**

## 受け入れ基準の充足

| issue の観点 | 結果 |
| --- | --- |
| 整形揺れを故意に入れた変異試験で CI が fail する | ✅ F1 / F2 / F3 |
| 既存ゲート（lint / typecheck / カバレッジ床）と重複・矛盾しない | ✅ lint は 0 errors のまま。prettier と ESLint の整形規則は衝突していない（`eslint-config-prettier` は不要） |
| 一括適用のコミットが DoD を満たす | ✅ 整形のみの独立コミットに分離（`507a244` = 設定と結線 / `29c6521` = 一括整形） |

## 申し送り

- **`.github/workflows/` を編集できたのは実行環境の差である**（#617）。
- **#491 / #493（Husky）は本件を代替しない** —— issue の指摘どおり、CI にゲートが無ければ
  前倒しする対象が存在しない。本 PR でその対象ができたので、第 5 段の Husky は本ゲートを手元へ前倒しできる。
