---
title: AST submodule を develop（3d35c7ac）へ前進させ、realm export の 255 文字修正を取り込む（integration-stack の Keycloak 起動失敗の解消）
issue: "#1372"
plan_refs:
  - NFR
adr_refs:
  - IADR-0056
  - IADR-0120
status: done
created: 2026-09-11
---

# 作業仕様書: AST submodule の前進（07792f3a → 3d35c7ac）

## 起点

- 前回の前進（#1420・AST 21ba8391）以後、基盤の integration-stack が Keycloak の realm import（AST export の description が varchar(255) 超・SQLSTATE 22001）で起動失敗していた（run 34609129031 / 34610152163）。AST#787（PR #788）で修正。
- ポインタ更新のみ。MSP 側に対応する変更は無い（IADR-0120）。基盤側の再発防止は MSP#1424。

## 取り込む内容

| AST コミット | 内容 |
| --- | --- |
| 3d35c7ac | fix(NFR-06,ADR-0038,IADR-0324): realm-export.json の写し注記を Keycloak の varchar(2 |

## 受け入れ基準

- [x] `git ls-files -s src/ai-stock-trading` が AST develop の先頭（3d35c7ac）
- [ ] develop の integration-stack で Keycloak が起動し、床入り（RESET_FLOOR=1）の T-10 所要時間が測れる

## 計画書との差異

差異なし。
