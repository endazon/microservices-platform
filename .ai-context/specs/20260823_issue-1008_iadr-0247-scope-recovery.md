---
title: 作業仕様書 — IADR-0247 の射程判断（BOM の除外根拠・不可視破壊 4 class）を回収する
type: spec
status: done
related_ids:
  - IADR-0247
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-standards.md
issue: "#1008"
---

# 作業仕様書: IADR-0247 の射程判断（BOM の除外根拠・不可視破壊 4 class）を回収する

> 本作業はコード実装ではなく、既存の `.ai-context/adr/IADR-0247_nul-byte-check.md` への
> 追記（記録の回収）である。起点となる計画書の FR/UC/SC は無い（メタ作業）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: 該当なし（メタ作業。`.claude/rules/traceability.md`「起点 ID の種別」
  `NFR` 項の 2 に該当し、無採番でよい）
- ユースケース（UC）: 該当なし
- 画面（SC）: 該当なし
- 関連 ADR: `IADR-0247`（本体）、`IADR-0138`（規約ベースの許可リスト回避の先例）、
  `IADR-0245`（`readSource` の BOM 復号）
- 計画書リンク: `planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-standards.md`
- 起票元 issue: `#1008`（本文に回収元 2 ファイルの全文が転記されている。回収元自体は
  worktree `C:\wt956` に untracked のまま残っており、issue 作成後に削除予定）

## 目的・背景

`#956` / `IADR-0247`（生の NUL バイト検査）の作業中に測った射程判断の根拠 2 件
（BOM を射程へ足さないと決めた根拠、「編集操作が不可視に壊す」4 class の射程判断）が
worktree に untracked のまま残っており、develop に着地していない。回収元ファイルは
issue #1008 作成後に削除される予定のため、issue 本文に転記された全文を IADR-0247 本体へ
正式に記録する。

## 対象範囲

- 対象: `.ai-context/adr/IADR-0247_nul-byte-check.md` への追記（frontmatter `updated:` の
  前進を含む）。本作業仕様書自身の新規作成。
- 対象外: 新規 IADR の起票（既存 IADR-0247 への追記で完結させる。「新しい IADR 番号は
  取らない」という担当上の制約に従う）。`.ai-context/adr/IADR-0238*`・
  `scripts/check-backend-libraries.js`・`.github/workflows/frontend-tests.yml`・
  `src/knowledge/backend/`（他エージェント担当領域、触らない）。

## 設計（追記方針）

- 追記書式は `.claude/rules/traceability.repo.md`「Superseded / Deprecated な ADR を
  引用するときの書式」に定める**日付つき追記ブロック** `［YYYY-MM-DD 追記 / #NNN］` を用いる。
  同節は「適用先は live な権威文書とコード（`.ai-context/adr/` に限らない）」としており、
  `.ai-context/adr/` 配下の凍結記録本体（`.ai-context/specs/`・`.ai-context/superpowers/`）とは
  異なり、ADR 本体は今回のような根拠追記が許される（`updated:` を前進させ、起票 ID を注記へ書く）。
- 追記位置: 既存の「検出しないこと」節はそのままとし、新規節
  `## ［2026-08-23 追記 / #1008］ 除外した射程の判断根拠` を「検出しないこと」の直後・
  「影響」の直前に追加する。既存の決定 1〜3・検出しないこと・代替案の本文は書き換えない
  （後付け追記のみ・本文プロズの書き換え禁止）。
- 内容: issue #1008 本文に転記された `bomnote.md` 全文（BOM 除外根拠）と `scope4.md` 全文
  （4 class の射程判断表と各 class の理由）を、IADR-0247 の記述スタイル（🔴 強調・表）に
  揃えて統合する。原文の実測値・理由付けは要約せずそのまま反映する。
- issue 番号の書式: 本リポジトリ自身の issue のため `.claude/rules/traceability.repo.md`
  「自リポを指す `MSP #266` は裸でよい」に従い、`#956` / `#439` / `#783` は裸表記のままとする。

## 受け入れ基準

- [ ] IADR-0247 に BOM の除外根拠（実測: UTF-8 BOM 15 件・UTF-16 BOM 0 件、EF Core 生成
      コードのみ、除外理由 3 点）が記録されている
- [ ] IADR-0247 に「編集操作が不可視に壊す」4 class の射程判断（表 + class 2〜4 の理由）が
      記録されている
- [ ] 追記が `［YYYY-MM-DD 追記 / #NNN］` 書式に従い、`updated:` が前進している
- [ ] 既存の決定 1〜3・検出しないこと・代替案の本文を書き換えていない
- [ ] `node scripts/check-doc-links.js` / `check-adr-numbering.js` / `check-cross-repo-refs.js` /
      `check-plan-id-qualification.js`（存在するもの）が通る

## テスト方針

- ドキュメント追記のみのため実行系テストは対象外。上記の文書機械検査（doc-links /
  adr-numbering / cross-repo-refs / plan-id-qualification）を検証コマンドとして実行する。

## 計画書との差異

- 差異: なし（本作業は実装ADRへの記録回収であり、計画書 `ADR-0030` の記述には影響しない）

## 未決事項

- 回収元ファイル（`C:\wt956/bomnote.md` / `scope4.md`）は本リポジトリの作業ツリー上には
  存在しない（別 worktree）ため、本作業では issue #1008 本文の転記を一次情報として扱う。
  差異があれば人間に確認する。
