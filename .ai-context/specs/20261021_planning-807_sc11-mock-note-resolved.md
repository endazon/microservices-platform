---
title: SC-11 の画面仕様書の「モックの文言は古い」注記を、計画のモックの是正（planning#807）に合わせて是正済みにする
type: spec
status: done
related_ids: [SC-11, FR-15, ADR-0127, ADR-0017, ADR-0027]
author: claude
created: 2026-10-21
updated: 2026-10-21
plan_refs:
  - planning:projects/microservices-platform/05_screens/mockups/hi-fi/sc-11.html（428・429 行。eb5e056 で是正）
  - planning:projects/microservices-platform/05_screens/mockups/wireframe/sc-11.html（同上）
  - planning:projects/microservices-platform/05_screens/01_screens.md（変更履歴 2026-10-21）
issue: "planning#807"
---

# 仕様書: SC-11 の画面仕様書の注記を計画のモックの是正に合わせる（planning#807）

> 本仕様書は着手前に作成した（2026-10-21）。計画は project-planning `origin/main` `577d21f`（是正の squash は `eb5e056`、PR planning#810）を読んだ。実装の基点は MSP `origin/develop` `0006c87`。
> 小さな docs の是正のため MSP の issue は起票せず、PR 本文で planning#807 を引く。

## 目的・背景

#1746（IADR-0560）で `docs/screens/SC-11_configuration-viewer.md` の対応表 #11 に「［2026-10-17］モックの文言『Ruri v3（高機密）』は古い。モックの是正は計画側が引き受ける」と注記した。
planning#807 の裁定（2026-10-21）で計画のモック（hi-fi・wireframe）の 2 行が改まった。

- 「ポート: 埋め込み」: 「Voyage AI（既定）／Ruri v3（高機密）」→「Voyage AI（public / internal）／語彙索引のみ（高機密・埋め込みなし）」（ADR-0127）
- 「イベント接続」: 「MassTransit / RabbitMQ」→「Wolverine / RabbitMQ」（ADR-0027）

行番号（428・429）は変わらない。注記は根拠を失ったので外し、日付つきで「是正済み」とする。

## 母集合（規則 9・10）

誤りの側の文字列で走査した（`git grep -n -E "Ruri v3（高機密）|MassTransit / RabbitMQ|モックの是正は計画|モックの文言" -- . ':!src/ai-stock-trading'`）。

| 該当 | 扱い |
| --- | --- |
| `docs/screens/SC-11_configuration-viewer.md:115`（対応表 #10） | 対象。モックの文言を是正後へ改め、是正済みを注記 |
| `docs/screens/SC-11_configuration-viewer.md:116`（対応表 #11） | 対象。2026-10-17 の注記を外し、是正済みを注記 |
| `.ai-context/specs/20261017_1746_*.md`（4 か所） | 除外。凍結記録（作業時点の記述） |
| `.ai-context/specs/20260805_issue-504_*.md:630` | 除外。凍結記録。SC-10 の副題で本件と無関係 |
| `docs/screens/SC-01_search-chat.md:98` | 除外。「モックの文言」の別件（SC-01 の出典行） |

`Voyage AI（既定）` `語彙索引のみ` でコード・テスト・ロケールも走査した。モックの文言を再現する箇所は無い（画面は構成が返す値をそのまま出す）。実装の変更は要らない。

規則 10: 是正で新たに誤りになる記述は、対応表の見出し「行番号は `d980a01` に対する」（文言が `eb5e056` になった）。同じ変更で注記を添えた。

## 受け入れ基準

- [x] #11 の 2026-10-17 の注記（「古い」「計画側が引き受ける」）が無くなり、2026-10-21 の是正済みが読める
- [x] #10 の文言が是正後（Wolverine）になっている
- [x] trace ブロックに `planning#807`・`ADR-0027` を足し、`updated:` を 2026-10-21 にした
- [x] `node scripts/check-trace-blocks.js` ほか文書系検査が通る
