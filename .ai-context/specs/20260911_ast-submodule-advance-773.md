---
title: AST submodule を develop（db3cfe89）へ前進させ、Discord /report の照会先修正を取り込む
issue: "#1372"
plan_refs:
  - NFR
adr_refs:
  - IADR-0056
  - IADR-0120
status: done
created: 2026-09-11
---

# 作業仕様書: AST submodule の前進（2e711b94 → db3cfe89）

## 起点

- AST#772（PR #773）の着地をポインタ更新で取り込む。MSP 側に対応する変更は無い（IADR-0120）。

## 取り込む内容

| AST コミット | 内容 |
| --- | --- |
| db3cfe89 | fix(FR-09,FR-14,UC-03,IADR-0240): 経路B の notification に Reports__BaseUrl を与え� |

## 受け入れ基準

- [x] `git ls-files -s src/ai-stock-trading` が AST develop の先頭（db3cfe89）
- [x] 文書検査器が緑
- [ ] CI が緑

## 計画書との差異

差異なし。
