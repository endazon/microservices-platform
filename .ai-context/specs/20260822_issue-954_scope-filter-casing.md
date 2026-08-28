---
title: scopeFilter.ts の改名 — 大小非区別 FS で .tsx が 2 本まるごとコンパイル対象から落ちる
type: spec
status: done
related_ids: [FR-04, FR-05, SC-01, SC-08, UC-01, UC-02, ADR-0031, IADR-0121, IADR-0125, IADR-0211]
author: claude
created: 2026-08-22
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
---

# 仕様書: scope-filter の大小衝突を解く（#954）

## 起点

欠陥修正（新規の FR/UC は無い）。**develop が Windows で緑にならない**ことの是正。

## 根本原因 —— 「ファイル名の衝突」ではなく「拡張子探索の衝突」である

`ScopeFilter.tsx` と `scopeFilter.ts` は**拡張子が違うので FS 上は衝突しない**（5 ファイルとも実在する）。
壊れているのは**解決**の側である。

`tsc --traceResolution` の実測（origin/develop f139b6ca / Windows 11 / Node 24.18.0 / TypeScript 5.9.3）:

```
Loading module as file / folder, candidate module location '.../scope-filter/ScopeFilter', ...
File '.../scope-filter/ScopeFilter.ts' exists - use it as a name resolution result.
======== Module name '../scope-filter/ScopeFilter' was successfully resolved to '.../scope-filter/ScopeFilter.ts'. ========
```

**`ScopeFilter.ts` はディスク上に存在しない。** 拡張子は `.ts` が `.tsx` より先に試され、その `fileExists('ScopeFilter.ts')`
が大小非区別 FS では `scopeFilter.ts` に当たる。よって `../scope-filter/ScopeFilter` は**モデル側へ束縛され**、
`ScopeFilter` コンポーネントは「存在しない export」になる。

### 影響は issue の記録より広い（実測で 3 つ目まで見えた）

`tsc --listFiles` の実測。**プログラムに入るのは 5 ファイル中 3 ファイルだけ**である:

| ディスク上 | tsc のプログラム |
| --- | --- |
| `ScopeFilter.tsx` | **入らない** |
| `ScopeFilter.test.tsx` | **入らない** |
| `scopeFilter.ts` | `ScopeFilter.ts` として入る（誤った casing） |
| `scopeFilter.test.ts` | 入る |
| `useScopeCandidates.ts` | 入る |

`include: ["src"]` のワイルドカード列挙が、同一ディレクトリの同名（大小非区別）・低優先拡張子として
**`.tsx` 2 本を捨てている。** したがって Windows では:

1. **型検査 EXIT=2 / 10 件**（issue の記録は 9 件。環境差は下記「issue との差分」）
2. **単体テスト EXIT=1 / 23 件失敗**（SC-01 の画面 15 件 ＋ `ScopeFilter` 自身 8 件）
3. **`ScopeFilter.tsx` と `ScopeFilter.test.tsx` は型検査を 1 度も受けていない** —— この 2 本の中の型エラーは
   Windows では**永久に報告されない**（issue に無い事実）
4. **knip が `Unused files (2)` を出す** —— 床（`scripts/knip-baseline.json`）に `files` 区分は無いので、
   `check-knip.js` の規約では「新区分」として fail する（issue に無い事実）

CI（Linux）は大小を区別するため、**1〜4 のいずれも CI からは見えない。**

## 決定 —— モデル側を `scopeSelection.ts` へ改名する

`scope-filter/` 以外の全 feature は、純粋ロジックの置き場を**ディレクトリ名の反復ではなく領域概念**で命名している
（実測: `citations.ts` / `attributes.ts` / `syncState.ts` / `jobStatus.ts` / `analysisRange.ts` / `abacVocabulary.ts` /
`opsTools.ts` / `driftView.ts` / `confidentiality.ts` / `department.ts` / `lifecycle.ts`）。
**自分のディレクトリ名を繰り返しているのは `scopeFilter.ts` ただ 1 本**であり、その反復がそのまま衝突である。

主たる型が `ScopeSelection` であることから `scopeSelection.ts` を採る。既存の慣習に戻すだけで、新しい規約を足さない。

- `scopeFilter.ts` → `scopeSelection.ts`
- `scopeFilter.test.ts` → `scopeSelection.test.ts`
- コンポーネント側（`ScopeFilter.tsx` / `ScopeFilter.test.tsx`）は**改名しない**（部品名とファイル名の一致を保つ）

別名へ寄せるため、**大小のみの変更ではない**。2 段階 `git mv` は不要である（1 回の `git mv` を実測で確認）。

## 追随

import 8 ファイル（`SearchChatPage.tsx` / `AnalysisDashboardPage.tsx` / `analysisRange.ts` / `ScopeFilter.tsx` /
`ScopeFilter.test.tsx` / `scopeSelection.test.ts` / `useScopeCandidates.ts`、および `abac/department.ts` のコメント 1 件）。

さらに**issue に列挙が無く、CI ゲートに触れる 2 系統**:

- **i18n カタログ**: `messages.po` の `#:` 参照 6 行（ja/en × 3）。`frontend.yml` の
  「i18n catalogs are up to date (lingui)」が `pnpm run i18n` 後の `git diff --exit-code` で見るため、
  再生成を伴わないと CI が赤になる。**実測: 差分は `#:` の 6 行のみ・未訳 0 件（393/393）。**
- **生きた文書**: `docs/tests/SC-01_search-chat.md:97` が旧ファイル名を書いている。改名で誤りになるので引き直す。

`.ai-context/specs/20260815_issue-767_*.md` も旧名に触れるが、**凍結記録（当時の事実）なので書き換えない**
（`.ai-context/` は本文プロズを後から書き換えない —— CLAUDE.md / ADR-0048 決定 1）。

## 再発防止 —— 足さない

CLAUDE.md「検査器の追加は同型の事故が 2 回起きたら」に照らし、**本件は 1 回目**である。
`git ls-files` を小文字化して重複を数える検査は安価だが、**2 例目を実測してから足す。**
走査の実測: 追跡下のファイルで「同一ディレクトリ・大小無視で同名・拡張子違い」の組は
**本件（`ScopeFilter`/`scopeFilter` の 2 組）以外に 0 件**。よって 2 例目はまだ無い。

## 完了条件（すべて実測で確認する）

- `pnpm run typecheck`（全ワークスペース）が EXIT=0
- `tsc --listFiles` に `ScopeFilter.tsx` と `ScopeFilter.test.tsx` が**現れる**
- 再現していた 23 件が通る（`vitest run .../scope-filter .../sc01-search` が 34 passed / skip 0）
- 全体テストが改名前と同じ結果（既知の未修正 1 件を除く）
- `pnpm run lint` EXIT=0 / `format:check` EXIT=0 / `pnpm run build` EXIT=0
- knip の区分件数が `knip-baseline.json` の床と**一致**する（`files` 区分が消える）
