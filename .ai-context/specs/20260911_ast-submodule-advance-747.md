---
title: AST submodule を develop（1da636de）へ前進させ、east-west gRPC の土台と Stage 0 の本番駆動を取り込む
issue: "#1372"
plan_refs:
  - NFR
adr_refs:
  - IADR-0056
  - IADR-0120
status: done
created: 2026-09-11
---

# 作業仕様書: AST submodule の前進（b0078560 → 1da636de）

## 起点

- AST 側で 2026-09-11 に着地した 2 コミットを本リポジトリの合成へ取り込む。MSP 側に対応する変更は無い。
  submodule の中身は編集しない（IADR-0120）。ポインタの更新のみ。

## 取り込む内容（AST develop の差分）

| AST コミット | 内容 |
| --- | --- |
| 66833fc2（AST#744） | east-west gRPC の土台（段 0・h2c リスナ・s2s チャネル・Helm `grpcPort` opt-in）。IADR-0328。既定描画はバイト等価 |
| 1da636de（AST#747） | Stage 0 判定を本番戦略で走らせる登録・分割固定（訓練カットオフ 2026-01-31・IS:OOS 2:3・PBO 4）。IADR-0329 |

## 受け入れ基準

- [x] `git ls-files -s src/ai-stock-trading` が AST develop の先頭（1da636de）
- [x] 文書検査器が緑
- [ ] CI（frontend の合成ビルド・backend）が緑

## 計画書との差異

差異なし（ポインタ更新のみ）。
