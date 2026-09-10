---
title: AST submodule を develop（6a9f71c3）へ前進させ、認証レルム統一・OpenD 起動モードの成果を取り込む
issue: "#1372"
plan_refs:
  - NFR
adr_refs:
  - IADR-0056
  - IADR-0120
status: done
created: 2026-09-10
---

# 作業仕様書: AST submodule の前進（a5e2fe64 → 6a9f71c3）

## 起点

- AST 側で着地した 4 コミット（AST#729 認証レルム統一・AST#733 OpenD 起動モード ほか）を本リポジトリの合成へ取り込む。
  MSP 側の対応（#1375 realm へ AST 客体・#1377 BFF の OpendAuth 配線）は既にマージ済みで、submodule だけが遅れている。
- 本リポジトリから submodule の中身は編集しない（IADR-0120）。前進はポインタの更新のみ。

## 取り込む内容（AST develop の差分）

| AST コミット | 内容 | MSP 側の対応 |
| --- | --- | --- |
| 879ba2cc（AST#729） | `values-local.yaml` の `global.authAuthority` を MSP レルムへ。IADR-0324 | #1375（realm へ `trading-service` / svc / owner） |
| 6a9f71c3（AST#733） | `OPEND_STDIN_MODE`（console / fifo / tty）と console 経路の tty 転送除去。IADR-0325 | — |
| 1dab577d | OpenD 認証まわりの作業仕様書の追随 | — |
| 10839810（AST#725） | LLM ゲートウェイ呼び出しへ MSP レルムの s2s トークン。IADR-0323 | #1368（`ai-stock-trading-llm-caller`） |

## 受け入れ基準

- [x] `git -C src/ai-stock-trading rev-parse HEAD` が AST develop の先頭（6a9f71c3）
- [x] `check-unit-service-ownership` ほか文書検査器が緑
- [ ] CI（frontend の合成ビルド・backend）が緑

## 計画書との差異

差異なし（ポインタ更新のみ）。
