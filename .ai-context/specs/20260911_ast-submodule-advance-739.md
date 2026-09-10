---
title: AST submodule を develop へ前進させ、s2s 認証の追随・日報自動生成・OpenD 再接続の成果を取り込む
issue: "#1372"
plan_refs:
  - NFR
adr_refs:
  - IADR-0056
  - IADR-0120
status: done
created: 2026-09-11
---

# 作業仕様書: AST submodule の前進（6a9f71c3 → b0078560）

## 起点

- AST 側で 2026-09-11 に着地した 6 コミットを本リポジトリの合成へ取り込む。MSP 側に対応する変更は無い（realm・BFF 配線は
  #1375 / #1377 で済んでいる）。submodule の中身は編集しない（IADR-0120）。ポインタの更新のみ。

## 取り込む内容（AST develop の差分）

| AST コミット | 内容 |
| --- | --- |
| 6aae4a47（AST#735） | LLM ゲートウェイ呼び出しの資格情報を `ai-stock-trading-llm-caller` へ（IADR-0323 追記） |
| b8c69a71（AST#737） | BFF 3 モジュールが上流 401 を 502 へ写像（IADR-0326） |
| 7cf797e7（AST#738） | s2s 発信者の `ServiceAuth__TokenEndpoint` を `global.authAuthority` から導出（IADR-0324 追記） |
| 3a721eff（AST#741） | 経路B で日報ドラフト自動生成を有効化（IADR-0115 決定 6 の opt-in） |
| e866f270（AST#739） | OpenD 接続失敗後に接続オブジェクトを作り直す（IADR-0327） |
| b0078560（AST#742） | NFR-01/02 実測の追跡先を #689 / #690 へ（docs） |

## 受け入れ基準

- [x] `git ls-files -s src/ai-stock-trading` が AST develop の先頭
- [x] 文書検査器が緑
- [ ] CI（frontend の合成ビルド・backend）が緑

## 計画書との差異

差異なし（ポインタ更新のみ）。
