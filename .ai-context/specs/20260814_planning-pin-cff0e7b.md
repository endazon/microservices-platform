---
title: 作業仕様書 planning pin を cff0e7b へ進める（ADR-0023 Accepted・着手ゲートの FR 別書き分けを取り込む）
type: spec
status: done
related_ids: [NFR, ADR-0023, ADR-0037, IADR-0119, IADR-0142]
author: Claude
created: 2026-08-14
updated: 2026-08-14
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
related_specs:
  - ../../docs/how-to/plan-id-range-history-annex.md
---

# 仕様書: planning pin を `cff0e7b` へ進める

## 起点となる ID（トレーサビリティ）

- 起点 ID: **NFR**（計画の追随。工程の規律＝メタ作業であり、当たる `NFR-xx` が無いため無採番とする。[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md)）
- 起点 issue: **#714**（`check-planning-pin-freshness` の定期検査が検出）
- pin: `2cf0795` → **`cff0e7b`**（10 コミット）
- 分類（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4 ＝ 監査強度の分岐）: **記録の追随のみ** → **全面 1 巡で打ち切り**

## 取り込む 10 コミット

| コミット | 対象 | 実装側への影響 |
| --- | --- | --- |
| `884eff8` / `14aed71`（planning#309 / planning#310） | **MSP** —— ADR-0023 の `Accepted` 化・着手ゲートの FR 別書き分け・00_vision / 01_problems の `fixed` 化 | **あり**（下記） |
| `0db9ae3`（planning#317） | **MSP** —— NFR の射程確定（メタ作業は無採番） | **反映済み**（#699 / [IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) が同じ裁定を先に取り込んでいる） |
| `690b0c7`（planning#322） | **MSP** —— 計画側 `07_adr/README.md` の IADR 対応表ドリフト解消 | なし（計画側の記録整備） |
| `1328a9b`（planning#306） | **MSP** —— SC-05 破壊的操作の統制の環流 2 件 | **反映済み**（#638 が実装済み） |
| `719ac6d`（planning#325） | kit —— 環流記録の status 語彙（`triaged` → `awaiting-decision`） | **反映済み**（#715 / [IADR-0185](../adr/IADR-0185_feedback-status-vocabulary.md)） |
| `2f350cd` / `599d58c`（planning#318 / planning#320） | kit —— companion 機構・計画 ID 修飾検査器・環流伝達検査 | なし（本リポは `check-plan-id-qualification.js` 等を導入済み） |
| `cff0e7b`（planning#326） | cross-project —— 運用ガイド **§11 複数実装リポのパリティ維持**の新設 | **あり**（運用。ガイドは本リポ CLAUDE.md が正本として参照する） |
| `b5aa0a1`（planning#321） | mondriq のみ | なし（別プロジェクトの名前空間） |

## 着手可否に効く変更（本 pin 前進の主眼）

1. **`ADR-0023`（エッジ TLS 証明書の自動化）が `Proposed` → `Accepted`**（2026-08-10・planning#308 起点の利用者裁定）。
   前提 ADR の `Accepted` を着手条件とする実装（cert-manager 導入・`ClusterIssuer`／`Certificate` 定義・
   Istio Gateway 連携）の**計画側ゲートが開いた**。計画は「**cert-manager 未配備は実装の未着手であって
   計画の未決ではない**」と明記している。
2. **着手ゲートの解消宣言が FR 別に書き分けられた**（02_requirements・2026-08-10）——
   **FR-17・FR-18 は解消／FR-19・FR-20 は SC-19 の本文編集導線を除いて着手可／FR-21 は起案段階のまま**
   （`fixed` 化条件は未定・別途裁定）。実装側の読み（[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) 決定 2 ／ [IADR-0142](../adr/IADR-0142_fr19-20-scoped-release-by-overturn-range.md)）と整合しており、
   **実装側の着手可否の結論は変わらない**。
3. **Wiki.js 前提検証の切り出し先が本リポ #602 と明記された**（02_requirements 注 2・ADR-0037 着手可否の注記。
   planning#314）。「誰が・いつ」は #602 が引き受ける。
4. **要求文・受け入れ基準・NFR（`NFR-01`〜`NFR-27`）は不変**。00_vision / 01_problems の `fixed` 化も本文変更なし。

## 計画 ID レンジの検査（pin 更新で最も壊れやすい点）

| 種別 | `2cf0795` | `cff0e7b` | 追随 |
| --- | --- | --- | --- |
| `FR` | FR-22 | **FR-22** | 不要 |
| `UC` | UC-11 | **UC-11** | 不要 |
| `SC` | SC-21 | **SC-21** | 不要 |
| 計画 ADR | 45 ファイル | **45 ファイル**（差分なし） | 不要 |
| `Proposed` な計画 ADR | 6 件 | **5 件**（`ADR-0023` が `Accepted` へ） | **要** —— `traceability.md` の走査基準行と別紙を更新した |

## 検証（実走した結果）

（結果は PR 本文に記載。`node scripts/check-planning-pin-freshness.js` が pin 前進後に検出 0 件となることを確認する）
