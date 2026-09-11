---
title: AST submodule を develop（2e711b94）へ前進させ、gRPC 段 1 の追随修正とテストの後始末を取り込む
issue: "#1372"
plan_refs:
  - NFR
adr_refs:
  - IADR-0056
  - IADR-0120
status: done
created: 2026-09-11
---

# 作業仕様書: AST submodule の前進（1c076147 → 2e711b94）

## 起点

- AST 側で 2026-09-11 に着地したコミットをポインタ更新で取り込む。MSP 側に対応する変更は無い（IADR-0120）。

## 取り込む内容

| AST コミット | 内容 |
| --- | --- |
| 2e711b94 | test(NFR): FoundationRegistrationTests が組み立てた TracerProvider/ServiceProvider � |
| 3bd7b872 | docs(NFR,IADR-0331): 取りこぼし分の行数を「計装行」で言い直し、ソー� |
| d17190e3 | fix(NFR,IADR-0331,IADR-0332): 生成 proto のカバレッジ除外をユニット名で引 |

## 受け入れ基準

- [x] `git ls-files -s src/ai-stock-trading` が AST develop の先頭（2e711b94）
- [x] 文書検査器が緑
- [ ] CI が緑

## 計画書との差異

差異なし。
