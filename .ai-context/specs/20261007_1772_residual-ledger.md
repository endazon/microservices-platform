---
title: 作業仕様書 — 計画 ADR-0105〜0129 の突合で見つかった残作業の台帳（#1772）を行ごとに切り出し・是正・受け皿の移管で埋める
type: spec
status: done
related_ids:
  - NFR
  - ADR-0020
  - ADR-0055
  - ADR-0081
  - ADR-0090
  - ADR-0107
  - ADR-0112
  - ADR-0115
  - ADR-0116
  - ADR-0117
  - ADR-0119
  - ADR-0121
  - ADR-0123
  - ADR-0124
  - ADR-0126
  - ADR-0129
  - IADR-0048
  - IADR-0203
  - IADR-0266
  - IADR-0414
  - IADR-0461
  - IADR-0468
  - IADR-0479
  - IADR-0498
author: claude
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0123_mcp-service-account-attributes-source-of-truth-is-idp.md (決定 2・3・フォローアップ 4)
  - planning:projects/microservices-platform/07_adr/ADR-0129_rerank-latency-target-nfr29-and-staged-rollout.md (決定 2・3・フォローアップ 1・3・4)
  - planning:projects/microservices-platform/07_adr/ADR-0081_ai-suggestion-generation-timing-constraints.md (決定 3・フォローアップ 2)
  - planning:projects/microservices-platform/07_adr/README.md (実装 IADR の対応表)
issue: "#1772"
---

# 作業仕様書 — 残作業の台帳 #1772 を埋める

## 目的と射程

第 4 回全体監査（2026-10-07）の観点 1（MSP 計画 ADR-0105〜0129）の台帳 #1772 の各行を、受け入れ基準どおり
「別 issue へ切り出した（番号）」「直した（PR）」「意図した保留として IADR に記録した」のいずれかにする。

- **本 PR で直すのは機械的な記録の是正だけである**: IADR の frontmatter `related_ids` への計画 ADR の追加（8 件）、
  計画 ADR が求めた IADR への日付つき追記（IADR-0479・IADR-0498）、画面仕様書 SC-12 の「繰り延べる」の改め（ADR-0123 フォローアップ 4）。
- **振る舞い・配備値・構成を変える行は、すべて別 issue へ切り出す**（1 issue = 1 PR。IADR-0116 規約 1）。
- 🔴 **planning#741 の裁定待ち（項目 4・5・6）には、どちらの向きにも寄せない。** `DepartmentAttributeSync__Mode` の配備値、
  運用仕様書の「既定無効」の節、ADR-0117 決定 4 の写しは変えない。受け皿を #1783（項目 4・5）と #1771（項目 6）に置く。
- 新しい IADR は起こさない。意図した保留（c）に当たる行が無く、全行が (a) か (b) で埋まったためである。

## 行ごとの処置（develop `260dc3de`・計画 `origin/main` で 2026-10-08 に実測）

| # | 台帳の行 | 実測（主張の確認） | 処置 |
| --- | --- | --- | --- |
| 1 | B-12 ADR-0116 決定 1 / ADR-0115 決定 3（部門属性の同期の既定） | 主張どおり。`DepartmentAttributeSyncOptions` の既定は `Off`、helm values・compose に `DepartmentAttributeSync` の値は 0 件（`deploy/` の該当は警報の説明文だけ）。運用仕様書は「既定無効・helm/compose に既定値を置いていない」と書く | **(a) #1783**（planning#741 項目 4・5 の受け皿。`blocked:decision`）。planning#741 へ移管をコメント済み。値は変えない |
| 2 | B-12 ADR-0119 実測 11 / ADR-0121 決定 4（内容の ABAC の門） | 主張どおり。`ContentAbac:Mode` 既定 Off・helm に値なし。Runbook（`docs/operations/ast-stale-copies-deletion-runbook.md`）は「記録」を「起点の issue にコメントで残す」とするが起点 #1667 は閉じている | **(a) #1784**（稼働での段 2・段 4 の実施と記録。記録先は #1784 に決め、Runbook の記録先の改めも同 issue の受け入れ基準に入れた。`blocked:env`） |
| 3 | B-14 ADR-0081 決定 3（`graph-suggestion`） | 主張どおり。加えて **`graph-cluster-summary` も未登録**（呼び出し側の用途名を全数走査: `rag-answer`・`analysis`・`diagram-coding`・`rerank` は登録済み）。行番号は `LlmRouter.cs:154-159`（用途が無いとエンドポイントの `DefaultModel`＝`claude-opus-5`）。さらに計測は未登録の用途を `other` に丸める（`LlmMetricValues.NormalizePurpose`）＝フォローアップ 2 の答えは「切り分けられない」 | **(a) #1785**。🔴 計画 ADR-0081 は用途のモデルを割り当てていない（決定 3 は費用の見方だけ）。割当が一意に決まらないため、構成（`PurposeModels`）は本 PR で変えない |
| 4 | ADR-0123 決定 2・3（SC-12 の Keycloak への入口） | 主張どおり。`McpServer/` に Keycloak 管理 API の呼び出しは無い | フォローアップ 1〜3 → **(a) #1786**。フォローアップ 4（IADR-0479 の「計画に定めが無い」・SC-12 の「繰り延べる」）→ **(b) 本 PR**（日付つき追記と画面仕様書の注記） |
| 5 | ADR-0107 決定 5 / ADR-0112 決定 3（インフラ製品の点検・digest） | 主張どおり。compose の `image:` 14 件中 digest は seaweedfs の 1 件。`deploy/` 全体でも重複除く 44 種中 1 種。運用仕様書の年次点検は ADR-0030 のライブラリだけ | **(a) #1787** |
| 6 | ADR-0090 決定 3（全 skip の緑の区別） | 主張どおり。`integration.yml` に実走 0 件を区別する門は無い（カバレッジ床の (b) は warn のみ） | **(a) #1788** |
| 7 | ADR-0129 決定 2 / フォローアップ 1・3（警報の切り替え・S3 の計測・IADR-0498 の追記） | 主張どおり（`alerts.yml:77-85` は 1.5 秒固定、計測結果なし、IADR-0498 に ADR-0129 の言及 0 件） | 計測（FU1）・警報の切り替え（FU3）・有効化（FU4）→ **(a) #1746** の段 S3・S5 へコメントで載せた（#1746 は S3・S5 を既に持つ open issue）。IADR-0498 の追記 → **(b) 本 PR** |
| 8 | ADR-0124 決定 2 の本番系（helm に Vault audit なし） | 主張どおり（helm で `audit` を走査して 0 件） | **(a) #1789**（本番 Vault 配備時に持ち込む。ADR-0126 FU3 と同じ issue。`blocked:env`） |
| 9 | ADR-0126 フォローアップ 2〜4 | 主張どおり（`values.yaml` の datasource `vault.address: ""`） | FU2（S4 移送）・FU4（残骸）→ **(a) #458** の段 S4 へコメントで確定。FU3（本番の address）→ **(a) #1789** |
| 10 | ADR-0117 決定 4（実行口の現状と計画の食い違い） | 主張どおり（3 サービスに実行口。DocumentService は門が閉じている間 `FAILED_PRECONDITION`） | **(a) #1783**（planning#741 項目 5 の受け皿。計画側の追記を受けて IADR-0479 へ追認を書くだけ） |
| 11 | 対応表の欠落（related_ids） | 5 件とも主張どおり（下の母集合） | **(b) 本 PR**。母集合の走査で 3 件を追加（計 8 件） |
| 12 | 記録のみ（C-5） | `HttpToolInvoker` は #1516（PR #1634 `c343b9d7`）で撤去済み。#1517 本文は 2026-10-07 に #1773 が追記済み。#1534 の Runbook・検査器の着地（PR #1736 `59dead84`）も 2026-10-07 に #1773 がコメント済み。`CS0618` は `IngestToSearchQdrantTests.cs:63` の 1 件（引数なしの `QdrantBuilder()` は v1.13.4 を起こし、配備の v1.18.1 と食い違う） | #1517・#1534 → 記録済み（重ねてコメントしない）。`CS0618` → **(a) #1790** |

planning#741 項目 6（`IngestionCompleted`）は台帳の行ではないが、受け皿が #1771（open。PR #1779 は `Refs`）であることを確かめた。

## 母集合（規則 9・10。`related_ids` の欠落）

**誤りの側から引く**: 「計画の対応表で `—`（実装 IADR なし）」を起点にし、その計画 ADR の本文が名指す IADR のうち
`related_ids` に当の計画 ADR を持たないものを数えた（`scratchpad` の一時スクリプト。計画 `origin/main`・develop `260dc3de`）。

1. 計画の対応表（`07_adr/README.md`、計画 `origin/main`）で `—` の計画 ADR は 7 件: ADR-0020・0055・0081・0090・0112・0123・0129。
   ［独立監査で訂正］当初は 11 件と数え、ADR-0024・0062・0107・0127 を含めていたが、対応表の実物ではこの 4 件は既に IADR を持つ
   （ADR-0107 は IADR-0461。IADR-0461 の `related_ids` も ADR-0107 を持つ）。11 件は対応表の古い写しから数えた誤りである。
2. （欠番。当初の手順 2・3 は、上の 4 件を除くための手順だった。7 件から始めれば不要であり、対象 8 件の結論は変わらない）
3. 7 件のうち ADR-0020 は本文が IADR を名指さないが、IADR-0048 の本文が「根拠 ADR: ADR-0020」と引く → 対象。
4. 対象（8 件）:

| IADR | 足す計画 ADR | 根拠 | 出所 |
| --- | --- | --- | --- |
| IADR-0048 | ADR-0020 | IADR-0048 本文が根拠 ADR として引く | 台帳 |
| IADR-0203 | ADR-0055 | ADR-0055 が「IADR-0203 決定 3 を追認する」 | 台帳 |
| IADR-0461 | ADR-0112 | ADR-0112 が「IADR-0461 決定 10 の 2 段を前提とする」 | 台帳 |
| IADR-0498 | ADR-0129 | ADR-0129 決定 3 が「IADR-0498 決定 11 を追認する」 | 台帳 |
| IADR-0468 | ADR-0115 | ADR-0115 が「IADR-0468 は本 ADR 決定 2 と同じ内容」 | 台帳 |
| IADR-0414 | ADR-0090 | ADR-0090 が「IADR-0414 の形を全リポの規則へ引き上げる」 | **走査で追加** |
| IADR-0266 | ADR-0081 | ADR-0081 §関連「IADR-0266（要求時・利用者スコープの選択。追認する）」 | **走査で追加** |
| IADR-0479 | ADR-0123 | ADR-0123 フォローアップ 4 が IADR-0479 の改めを求め、決定 1 が同 IADR の判定と一致 | **走査で追加** |

**除外したもの（理由）**:

- 計画 ADR-0105〜0129 の本文が名指す IADR のうち、当の ADR を `related_ids` に持たないものは上の 8 件の外に **61 組**ある
  （走査がそのまま返すのは着手前の 65 組。うち本 PR で足した 4 組〔ADR-0112・0115・0123・0129〕を引いた数。例: ADR-0105 → IADR-0152 ほか）。いずれも**計画 ADR が既に 1 件以上の IADR と結ばれている**（対応表で `—` にならない）か、
  本文の引用が「前提として扱う・背景」であり当の決定の実装ではない。台帳の行は「対応表で `—` になる」欠落を対象とするため、射程の外とする。
- ADR-0081 の残り（IADR-0276・0300・0323・0364・0380）は「前提として扱う」の列挙であり、決定の追認は IADR-0266 だけである。

**この変更で新たに誤りになる自分の記述（規則 10）**: なし。追記した IADR-0479・IADR-0498・SC-12 の本文は、切り出した issue 番号（#1786・#1746）を引く。
これらの issue が閉じても参照は切れない。`updated:` は 8 件とも 2026-10-08 へ進めた（前例: IADR-0425・IADR-0430 の `related_ids` 追加〔#1669〕）。

## 凍結記録の扱い

- `related_ids` の追加は frontmatter の変更であり、本文の凍結の対象外（前例: #1669 の IADR-0425・IADR-0430、#1685 の IADR-0453）。
- IADR の本文には `［2026-10-08 追記 / #1772］` の日付つき追記だけを足し、既存の文は書き換えない（前例: IADR-0425 の #1663 の追記）。
- 画面仕様書 SC-12 は `docs/` の生きた文書なので、注記と未決事項の解消を本文へ書く。計画 ID は trace ブロック（`adrs:`）へ入れ、表示テキストには書かない。

## 検証

- `node scripts/check-trace-blocks.js`・`node scripts/gen-knowledge-graph.js --check`・`node scripts/check-cross-repo-refs.js`・
  `node scripts/check-plan-id-qualification.js`・`node scripts/check-reading-budget.js`・`node scripts/check-doc-type-vocabulary.js`・
  `node scripts/check-commit-messages.js`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（`scripts.repo.test.js` は companion であり、単体で走らせると設計どおり exit 1 で拒否するので、`scripts.test.js` 経由で走らせる）。
- `.cs`・構成の変更は無いので `dotnet build` / `format` / `test` は対象外。

## 受け入れ基準

- [x] 台帳の 12 行すべてが (a) 切り出し（番号）か (b) 本 PR の是正になる（上の表）。
- [x] planning#741 項目 4・5 の MSP 側の受け皿が台帳の外（#1783）にあり、planning#741 へ移管を 1 度コメントした。
- [x] 裁定待ちの項目に、配備値・構成・計画の暫定手段の変更を入れていない。
- [x] `related_ids` の追加は母集合と除外理由つきで本書に残る。
