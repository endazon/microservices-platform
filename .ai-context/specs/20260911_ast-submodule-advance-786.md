---
title: AST submodule を develop（07792f3a）へ前進させ、スクリーニング解析器の修正を取り込む
issue: "#1372"
plan_refs:
  - NFR
adr_refs:
  - IADR-0056
  - IADR-0120
status: done
created: 2026-09-11
---

# 作業仕様書: AST submodule の前進（21ba8391 → 07792f3a）

## 起点

- AST#785（PR #786）の着地をポインタ更新で取り込む。MSP 側に対応する変更は無い（IADR-0120）。

## 取り込む内容

| AST コミット | 内容 |
| --- | --- |
| 07792f3a | fix(FR-04,FR-11,IADR-0248): 一次スクリーニングの Hold 出力（数値項目 null� |

## 受け入れ基準

- [x] `git ls-files -s src/ai-stock-trading` が AST develop の先頭（07792f3a）
- [x] 文書検査器が緑
- [ ] CI が緑

## 計画書との差異

差異なし。
