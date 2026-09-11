---
title: AST submodule を develop（21ba8391）へ前進させ、レルム一致検査・PBO 評価不能・GC 設定を取り込む
issue: "#1372"
plan_refs:
  - NFR
adr_refs:
  - IADR-0056
  - IADR-0120
status: done
created: 2026-09-11
---

# 作業仕様書: AST submodule の前進（db3cfe89 → 21ba8391）

## 起点

- AST 側で 2026-09-11 に着地したコミット（計画 ADR-0038 / ADR-0039 への対応と GC 設定）をポインタ更新で取り込む。MSP 側に対応する変更は無い（IADR-0120）。

## 取り込む内容

| AST コミット | 内容 |
| --- | --- |
| 21ba8391 | feat(FR-15,FR-20,ADR-0008,ADR-0039,IADR-0337): 探索を持たない記録再生では PBO |
| 1abfe22c | feat(NFR-05,ADR-0038,IADR-0324): 配備される全経路が同じレルムを指すこと� |
| a98f87de | fix(NFR-01,ADR-0006): 全 .NET サービスに Workstation GC と GCConserveMemory を入� |

## 受け入れ基準

- [x] `git ls-files -s src/ai-stock-trading` が AST develop の先頭（21ba8391）
- [x] 文書検査器が緑
- [ ] CI が緑

## 計画書との差異

差異なし。
