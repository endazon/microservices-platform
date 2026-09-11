---
title: AST submodule を develop（1c076147）へ前進させ、gRPC 段 1・LlmGateway gRPC・トレース秘匿・検査器の成果を取り込む
issue: "#1372"
plan_refs:
  - NFR
adr_refs:
  - IADR-0056
  - IADR-0120
status: done
created: 2026-09-11
---

# 作業仕様書: AST submodule の前進（1da636de → 1c076147）

## 起点

- AST 側で 2026-09-11 に着地したコミットを本リポジトリの合成へ取り込む。MSP 側に対応する変更は無い。
  submodule の中身は編集しない（IADR-0120）。ポインタの更新のみ。

## 取り込む内容（AST develop の差分）

| AST コミット | 内容 |
| --- | --- |
| 1c076147 | refactor(NFR,IADR-0284,IADR-0328): east-west gRPC 段 1 として全体前提条件の同� |
| 1cf92ed8 | refactor(NFR,FR-04,IADR-0323,IADR-0328): LlmGateway の POST /complete 2 経路へ east-wes |
| bdff6736 | fix(NFR): 補間文字列のホールをコードとして残し、前処理に直接試験 |
| 0e60fa27 | fix(FR-04,NFR-05,IADR-0323): 本番 values.yaml の LlmGateway 資格情報を llm-caller � |
| 8f1fb0c4 | test(NFR): DI 登録済みで本番の呼び出し元がゼロの型を検知する検査� |
| 9dc52b0a | fix(NFR,FR-09,IADR-0121,IADR-0333): 資格情報を含む URI をトレースの url.full � |
| 2343f624 | fix(NFR): check-test-traceability の T1 母数判定を大文字小文字非依存にす� |
| a6d8147d | feat(FR-06,FR-15,ADR-0037): 月報 §7 へ stage0-recording の費用実績と見積り承� |
| 67ec624c | test(FR-05,ADR-0019,ADR-0026): 偽 OpenD に対する moomoo アダプタ結合試験を既 |
| ea21571e | fix(FR-15,ADR-0002,IADR-0327): 履歴 K 線経路も OpenD 接続失敗後に接続オブ� |

## 受け入れ基準

- [x] `git ls-files -s src/ai-stock-trading` が AST develop の先頭（1c076147）
- [x] 文書検査器が緑
- [ ] CI（frontend の合成ビルド・backend）が緑

## 計画書との差異

差異なし（ポインタ更新のみ）。
