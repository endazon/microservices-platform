---
title: 作業仕様書 — DocumentUpdated 購読による ABAC 属性のデノーマライズと本文指紋（#911）
type: spec
status: done
related_ids:
  - FR-17
  - FR-05
  - FR-18
  - UC-10
  - ADR-0033
  - ADR-0050
  - ADR-0027
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "ADR-0033 決定 2（属性の非正規化保持・イベント購読で即時更新）・決定 10（却下解除）"
  - "ADR-0050（DocumentUpdated は本文指紋を運ぶ。決定 2: UpdatedAt では判定しない。決定 4: 移行 → 契約変更の順）"
related_adrs:
  - IADR-0242
  - IADR-0245
issue: "#911"
---

# 作業仕様書: ABAC 属性のデノーマライズ（#911）

## 起点と前提

- 前提の E3b（辺 `DocumentUpdated` の Wolverine 化）は完了済み（`20260828_edge-e3b-document-updated.md`）。
  ADR-0050 決定 4「**移行 → 契約変更**」の順のとおり、本作業の契約変更は移行後に行う。
- 土台（`GraphDocument` の TryApply / BodyHash / 順序ガード、`AiSuggestion.TryReinstate`）は
  #908〜#914 で実装済み。本作業はその**発火経路**を配線する。

## 実装

| 変更 | 内容 |
| --- | --- |
| 契約 | `DocumentUpdated` へ `string? ContentFingerprint = null` を**末尾・既定値付き**で追加（ADR-0050 決定 1。IADR-0122 決定 2 の非破壊条件）。null = 発行側が指紋化できなかった（本文なし・ストレージ縮退） |
| DocumentService | `Document.ContentFingerprint` 列（migration `AddContentFingerprint`）。指紋 = 正規化 Markdown の UTF-8 SHA-256 小文字 hex（`DocumentBodyIntake.Fingerprint`。Obsidian 同期の ContentHash と同一計算に集約）。設定点: 本文つき登録・本文投入 PUT・Obsidian 同期（初回・版適用）・DocumentNormalized 受信（本文をストレージから読んで計算。CanResolve=false なら null）。発行ポート（IDocumentUpdatedPublisher）が指紋を運ぶ |
| GraphService | `GraphDocumentSyncConsumer`（graph-sync 段・`IPipelineStep<DocumentUpdated>`）: `graph_documents` へ upsert（順序ガード = TryApply）。指紋が**変わったときだけ**当該文書を端点とする却下済み提案へ `TryReinstate`（判定は UpdatedAt を用いない —— ADR-0050 決定 2） |
| S4 | DocumentUpdated の購読先へ knowledge/GraphService（wolverine）を追加 |
| S5 | graph-sync 段を追加（steps=8） |
| S6 | 変更なし（GraphService の RabbitMq 接続は #1016 = C2 で配線済み） |

## 鮮度契約（issue #911 の 4 点。GraphDocumentSyncConsumer 冒頭に同文を明記）

1. 判定は常に「最後に受信・適用した属性」に対して行う（同期照会の補正なし）。
2. **失敗方向は非対称**: 緩和の遅延は安全側・**厳格化の遅延は stale-allow の漏えい窓**。
   WikiService の ABAC 同期が既に持つ同型の受容であり、本サービスが新たに悪化させない。
3. 属性レコードが無いノードは不可視（fail-closed。IADR-0242 決定 12-3）。
4. `UpdatedAt` の順序ガードで古いイベントを適用しない（冪等・追い越し耐性）。

## #914 から写した発火の受け入れ基準（AiSuggestionWiringTests の指示どおり）

旧 `AiSuggestionWiringTests`（未配線を本番ソース走査で固定する装置）は、**配線した本 PR で
所定の手順どおり削除した**。同テストの失敗メッセージが求める 3 点:

1. テスト削除 → 実施（`Knowledge.IntegrationTests/Deployment/AiSuggestionWiringTests.cs`）。
2. **発火の受け入れ基準を写す** → 「本文指紋が変われば pending へ戻る（呼び出し側は本購読）」を
   `docs/tests/FR-18_ai-suggestions.md` T-15 へ書き換え、`GraphDocumentSyncConsumerTests` が写像する
   （coverage 床も更新済み）。
3. **UpdatedAt での代用を採らない** → 実装・テストとも指紋の変化のみ
   （`本文が変わらない更新では却下が解除されない_UpdatedAtでは判定しない`）。

## 受け入れ基準（issue #911）と写像

- [x] 古い更新時刻のイベントが適用されない（追い越し・再配信の冪等性）→
      `保持中より古いイベントは適用されない_厳格化後に緩和が復活しない` / `同一イベントの再配信は結果を変えない_冪等`
- [x] 属性レコードの無いノードが探索に現れない → 既存の fail-closed（IADR-0242 決定 12-3。
      AuthorizedGraphViewTests / GraphTraversalTests が固定済み。本購読はレコードの供給側）
- [x] 厳格化イベント適用後、直ちに当該ノードが不可視になる →
      `厳格化イベント適用後は直ちに不可視になる`（AbacNodeFilter の判定が複製属性で機能する正例/負例つき）
- [x] 本文ハッシュが本文変化で変わり、無変化で変わらない →
      `ContentFingerprintTests`（DocumentService 側。純粋関数の性質＋イベントが運ぶ値の遷移）

## 検査の状態

- `check-contract-schema.js`: **exit 1（非破壊 1 件: memberAdded ContentFingerprint）。**
  指示（契約 baseline JSON の `--update` はしない）に従い baseline を更新していない ——
  更新は `node scripts/check-contract-schema.js --update` 1 コマンド（差分は本追加のみ）。
- `check-event-topology.js` / `validate-pipeline-config.js` / `check-backend-libraries.js` /
  `check-trace-blocks.js` / `check-test-traceability.js` / `check-test-spec-coverage.js`: 緑。

## 変異試験（実測は締めのコミットまでに本節へ追記）

- 変異 A1: TryApply の順序ガード（`updatedAt < UpdatedAt`）を外す → 追い越しテストが赤。
- 変異 A2: 解除判定の「指紋が変わったときだけ」を外す（常に解除を試みる）→
  `本文が変わらない更新では…` は TryReinstate 側の同値比較が二重に守るため緑のまま**になり得る**。
  実測して二重防御の内訳を記録する。

［2026-08-28 追記 / #1021］**実測（波 2 監査の指摘 R1 の回収）:**

- A1 実測: `GraphDocument.TryApply` の順序ガードを除去 → `GraphDocumentSyncConsumerTests`
  **Failed 2 / Passed 6**（`保持中より古いイベントは適用されない_厳格化後に緩和が復活しない` と
  `古いイベントでは却下が解除されない_順序ガードが先に効く` が赤）。予告どおり kill。
- A2 実測: 消費側の指紋変化ゲート（`previousHash` との序数比較）を外し常に解除判定へ進める →
  **8 件すべて緑のまま（変異生存）**。予告どおりであり、**二重防御の内訳**は次のとおり:
  1 枚目（消費側ゲート）は却下済み提案の**走査を省く費用最適化**、2 枚目（ドメイン
  `AiSuggestion.TryReinstate`）が**却下時に記録した両端指紋と現在指紋の同値比較**で
  `!sourceChanged && !targetChanged → false` を返す**意味上の正**である。ゲートを外しても
  挙動は変わらず（走査費用だけ増える）、不変条件はドメイン側が単独で守る。
  **この変異生存は受容する**（テストで殺すべき欠陥ではなく、防御の所在の確認である）。
- いずれも変異を戻して緑へ復帰することを確認済み。

## 計画書との差異

差異なし。ADR-0050 決定 3（同じ指紋を再取り込みの要否判定に使う）は**本 PR では実装しない**
（「時期は実装側が別途計画してよい」—— IngestionService の無条件再索引は現状維持。将来の別単位）。
