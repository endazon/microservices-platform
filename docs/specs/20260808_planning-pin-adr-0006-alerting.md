---
title: 作業仕様書 planning pin を d9c2014 へ進める（上限アラートと SLO 一次検知の暫定統制が確定）
type: spec
status: done
related_ids: [NFR, ADR-0006, ADR-0044]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md"
  - "../../planning/projects/microservices-platform/06_technical/05_observability-ops.md"
related_specs:
  - ../how-to/session-handoff.md
---

# 仕様書: planning pin を `d9c2014` へ進める

## 起点となる ID（トレーサビリティ）

- 起点 ID: **NFR**（計画の追随）。起点 issue は無い（利用者の指示「project planning は定期的に更新されるので都度確認」）
- pin: `891b199` → **`d9c2014`**（4 コミット）
- 分類（[[IADR-0141]] 決定 4 ＝ **監査強度**の分岐）: **記録の追随のみ** → **全面 1 巡で打ち切り**

## 取り込む 4 コミット

| コミット | 対象 | 実装側への影響 |
| --- | --- | --- |
| `b8002cc` | **MSP** —— 上限アラートの実現経路・暫定統制 | **あり**（下記） |
| `2791e4f` / `c2998a6` / `d9c2014` | AST のみ（最小期待利益・可用性 NFR・FR-21 の粒度） | **なし**（別プロジェクトの名前空間） |

## 計画 ID レンジの検査（pin 更新で最も壊れやすい点）

`.claude/rules/traceability.md` の「起点 ID の種別」節はレンジを持つ。**変化していれば追随が要る。**

| 種別 | `891b199` | `d9c2014` | 追随 |
| --- | --- | --- | --- |
| `FR` | FR-22 | **FR-22** | 不要 |
| `UC` | UC-11 | **UC-11** | 不要 |
| `SC` | SC-21 | **SC-21** | 不要 |
| 計画 ADR | 45 ファイル | **45 ファイル**（差分なし） | 不要 |

**レンジは 1 つも動いていない**ため、`traceability.md` は無改変とする。
**「動いていないことを実測した」ことも結果である**（#599 の追記が同じ形を採っている）。

## 新しく確定した裁定（決定 39〜42）

`b8002cc` は **INDEX に決定 39〜42 を追加**した。実装側に宿題が生じるのは次の 2 件である。

| 決定 | 内容 | 実装側の担い手 |
| --- | --- | --- |
| **39** | Alertmanager 配備までの LLM 費用の統制を**月次の手動確認**とする。**運用仕様書（`docs/operations/`）へ手順・担当・記録と「現時点では自動検知が無い」ことを明記させる** | **#546**（計画が明示的に「実装 microservices-platform#546 で追跡する」と書いている） |
| **42** | SLO の一次検知は、Alertmanager 配備までの間 **Grafana の内蔵アラート**を通知先とする。**暫定期間も NFR「検出 5 分以内」を満たす**（欠けているのは通知の配線 1 点） | **#546** |
| 40 | 統制を定める記述には**現在の実現手段を併記**し、未配備なら条件付きに書いて暫定手段を並べる（planning 側 `CLAUDE.md` の規則） | 実装側の文書にも同型が在る（下記） |
| 41 | 月次予算の金額は**実測後に確定**。暫定期間は絶対額を持たず前月比で見る | —（実測待ち） |

### 実装側で同型が見つかった箇所（**本 PR では直さない**）

決定 40 が名指しした型は、実装側の `docs/operations/operations.md` にもそのまま在る
—— **統制として「通知経路は Alertmanager」と書きながら、同じ文書が「アラートルールの実配線は未了」と述べている**
（`operations.md:523` / `:534-538` の SLO 表 vs `:553` / `:620`）。

**本 PR では是正しない。** 理由は 2 つある。

1. **計画がこの追随を #546 の射程と明示している**（「配備時期は実環境の判断であり、実装 microservices-platform#546 で追跡する」）。
2. **pin 更新（記録の追随のみ）と運用仕様の改訂（規約の適用）は監査強度が違う**
   （[[IADR-0141]] 決定 4）。同じ PR に載せると重い方に引きずられ、pin 更新が滞る。

**#546 へ裁定の内容をコメントで残し、追跡できるようにした**（[[IADR-0139]] / #589 が問題にしている
「計画側の裁定が反映されたことを検知する経路が無い」への、**当座の人手による埋め合わせ**である）。

## 検証（実走した結果）

**本表は最終コミットの内容（本仕様書自身を含む）に対して取り直した値である**
（#620 のレビュー 🟢 の指摘。**当初は本書を足す前に測った値を持ち越しており、2 つの検査で 1 件ずつずれていた**
—— 判定〔違反 0 件〕は同じだが、**検証の記録としては誤り**である。走査対象は「今コミットする内容」でなければならない）。

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-doc-links.js --require-planning` | **OK 478 件**（planning を populate した状態で実行） |
| `node scripts/check-cross-repo-refs.js` | OK **555 件** |
| `node scripts/check-plan-id-qualification.js` | OK 1184 件 |
| `node scripts/check-adr-numbering.js` | OK |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **293 passed** |

> **`--require-planning` を使ったのは、pin 更新で壊れるとしたらそこだから**である。
> 既定の `check-doc-links.js` は planning へのリンクを検査対象にしない
> （`.claude/rules/traceability.md` が述べるとおり、CI の決定的ジョブは submodule を populate しない）。
> **本 PR ではローカルで populate 済みなので、`--require-planning` まで走らせた。**
