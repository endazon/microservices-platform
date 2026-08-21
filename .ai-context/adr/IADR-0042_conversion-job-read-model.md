---
title: IADR-0042 変換ジョブ読み取りモデル（インメモリ）と状況照会・人手補正 API
type: impl-adr
status: Accepted
related_ids:
  - SC-07
  - UC-06
  - FR-12
  - ADR-0003
  - ADR-0027
  - ADR-0012
  - IADR-0039
  - IADR-0127
  - IADR-0128
  - IADR-0154
author: claude
created: 2026-07-09
updated: 2026-08-10
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
---

# IADR-0042: 変換ジョブ読み取りモデル（インメモリ）と状況照会・人手補正 API

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: SC-07（変換ジョブ）／ UC-06 ／ FR-12（文書正規化）
- 関連 ADR: ADR-0003（MassTransit。Superseded by ADR-0027・注記は #580）／ ADR-0012（変換・pandoc）／ [IADR-0039](./IADR-0039_datasource-management-bff-and-role-gating.md)（管理系ロール）／ [IADR-0029](./IADR-0029_config-info-api-placement-and-drift-granularity.md)（ワーカーの最小 HTTP サーフェス）／ **[IADR-0127](./IADR-0127_sc07-retry-admin-only-and-derived-states.md)（画面側の retry 管理者限定）・[IADR-0128](./IADR-0128_conversion-retry-admin-only-and-downstream-posture.md)（API 側の retry 管理者限定）** ＝ いずれも本 IADR 決定 3 の retry を部分改定する
- 関連仕様書: `docs/screens/SC-07_conversion-jobs.md`

## コンテキストと課題

> **★［2026-08-10 追記 / #553］planning#198 提案 8 の裁定が出た。**
> **計画側が実装へ合わせて改訂された**（利用者裁定 2026-08-05・質問票 第12回 Q19）——
> 計画 `05_screens/01_screens.md:314` は現在「**閲覧は管理者・運用者。再変換の実行と人手補正は管理者限定**」である。
> 本文の「planning#198 提案 8 の裁定に従う」は**当時の記録**として残す。

SC-07 は変換状況・失敗ジョブの一覧と人手補正（再変換）を提供する。しかし **ConversionService はイベント駆動の fire-and-forget ワーカー**であり、変換状況を問い合わせる手段が無い（成功は `DocumentNormalized` を発行するのみ、失敗は MassTransit のデッドレターへ送られるが照会 API は無い）。Issue #133 の前提「バックエンド API は実装済み」は変換ジョブに関しては成立していなかった（計画とのギャップ）。

決めること:
1. 変換状況をどこに・どう保持するか。
2. 照会・人手補正の API 形。
3. 認可。

## 決定

1. **ConversionService に変換ジョブの読み取りモデルを追加する。** 変換コンシューマ（`RawDocumentFetchedConsumer`）が受信・成功・失敗の各ライフサイクルを `IConversionJobStore` に記録する。失敗時は記録後に例外を**再送出**し、MassTransit の再試行→デッドレター挙動は変えない（記録は可視化・人手補正のためのサイドカー）。
2. **MVP はインメモリ実装（`InMemoryConversionJobStore`・singleton）**とする。永続化（Postgres+EF）・複数インスタンス共有は follow-up（下記）。理由: 本 PR の主眼は SC-07 画面のエンドツーエンド実現であり、新規 DB スキーマ＋マイグレーション導入は画面実装フェーズとしては過剰・高リスク。ワーカーは現状単一インスタンス（dev）で、状況ビューの MVP としてインメモリで十分機能する。
3. **API（メッシュ内部・ワーカー上）**: `GET /jobs`（`?status=` 絞り込み）・`GET /jobs/{id}`・`POST /jobs/{id}/retry`（原本イベント `RawDocumentFetched` を再発行して再変換）。ワーカー自身は最小 HTTP サーフェスに留め認可は課さない（[IADR-0029](./IADR-0029_config-info-api-placement-and-drift-granularity.md) と同方針。ingress 非公開）。**認可は BFF で管理者・運用者に限定**（`/bff/conversion/jobs`。[IADR-0039](./IADR-0039_datasource-management-bff-and-role-gating.md)）。フロントは `RequireRole` で存在秘匿。

   > **［2026-08-05 追記・retry は「管理者・運用者」の例外（画面側 #503 / [IADR-0127](IADR-0127_sc07-retry-admin-only-and-derived-states.md) 決定 1、API 側 #501 / [IADR-0128](IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) 決定 1）］**
   > 計画（05_screens §SC-07 §データソース（計画リポ）・**2026-08-04 確定**。
   > planning `d980a01` の `01_screens.md:257`）が
   > 「**再変換の実行権限は管理者ロールに限る**。本画面のアクセス制御と API の権限を揃える」と定めた。
   > よって本決定 3 の「認可は BFF で管理者・運用者に限定」は、**`POST /bff/conversion/jobs/{id}/retry` には適用されない**。
   > retry は **`platform-admin` のみ**である。
   > **是正は画面側・API 側の両方で完了している**——画面のボタンは #503（PR #508。[IADR-0127](./IADR-0127_sc07-retry-admin-only-and-derived-states.md) 決定 1）が、
   > API の認可は #501（[IADR-0128](./IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) 決定 1。グループの認可へ `PlatformAuthPolicies.AdminOnly` を重ね、
   > AND 合成で admin のみに絞る）が担った。計画確定事項「画面と API の権限を揃える」は**満たされている**。
   > **照会（`GET /jobs`・`GET /jobs/{id}`）は本決定のまま「管理者・運用者」で据え置く** ——
   > 2026-08-04 の確定が命じたのは再変換の実行権限の是正だからである。
   > **照会が計画（`01_screens.md:115`・`:234` / `:242` / `:250` の「管理者ロール限定」）と食い違っている点は既知の逸脱**
   > （[IADR-0039](./IADR-0039_datasource-management-bff-and-role-gating.md) 決定 1 由来）であり、その是正の向き（計画改訂か実装是正か）は
   > **planning#198 提案 8 の裁定に従う**（[IADR-0128](./IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) 決定 2）。
   > **ワーカー自身に認可を課さない点は変更していない**（[IADR-0128](./IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) 決定 3）。
   > その前提であるネットワーク分離は `NetworkIsolationTests` の回帰ガードへ載せた。
   > **本決定は `Accepted` のまま有効**であり、上記 2 点（retry の例外化・下流の代償統制の明文化）だけが部分改定である。

   > **［2026-08-10 追記・表題の「人手補正 API」の実体は [IADR-0154](IADR-0154_manual-figure-correction-phase1.md) が定める（#543 / #545）］**
   > 本 ADR の**表題**は「変換ジョブ読み取りモデル（インメモリ）と状況照会・**人手補正 API**」であるが、
   > **本決定 3 が列挙する口は `GET /jobs`・`GET /jobs/{id}`・`POST /jobs/{id}/retry` の 3 本**である。
   > 表題に残る「人手補正 API」は**設計時点の想定であって、本 ADR が実装を決めた口ではない**
   > （2026-08-05 の実測。計画側 `01_screens.md:327` の突合結論と一致する）。
   > **表題は書き換えない** —— 当時の決定と想定を、当時の言葉のまま残すためである。
   > **実体は後継 [IADR-0154](./IADR-0154_manual-figure-correction-phase1.md) が定める**。同 ADR は計画の 2026-08-05 確定（**Phase 1 = 図のコード化の
   > やり直し**・**補正版を正とする**・**マージは採らない**）に基づき、本 ADR が想定していた口とは
   > **範囲の違う別の決定**を置いている。
   > **本 ADR の決定 1〜3 は引き続き有効**である（読み取りモデル・照会・再変換）。

## 根拠 / 代替案

- **DB 永続化を今回見送る**: 正しい最終形だが、マイグレーション生成・EF 配線・ワーカーのテスト整備を伴い本フェーズには重い。インメモリ MVP＋明確な follow-up が費用対効果に優れる。挙動（状態遷移・絞り込み・再変換）は `IConversionJobStore` の抽象で表現し、後日 EF 実装へ差し替え可能に設計した。
- **デッドレター直接照会を採らない**: RabbitMQ の `_error` キューは運用ツール向けで、画面の状況一覧・再変換 UX には不向き。読み取りモデルの方が UC-06 に合致する。
- **リトライ挙動は不変**: 失敗記録後に必ず再送出し、既存の再試行→デッドレター（[[ADR-0003]]。Superseded by ADR-0027・注記は #580）を保持する。

## 影響

- `Shared.Contracts` に `ConversionJobDto` / `ConversionJobStatus`。
- ConversionService に `IConversionJobStore` + インメモリ実装、コンシューマの記録、`/jobs` エンドポイント。
- BFF に `ConversionBffEndpoints`（照会は admin/operator 限定・**retry は admin のみ**〔上記［追記］〕）＋ `ConversionService` named client。
- フロント `features/sc07-conversions`（`/admin/conversions`・admin/operator 限定・状況一覧・フィルタ・**再変換ボタンは admin のみ**〔上記［追記］〕）。

## フォローアップ（計画へのフィードバック）

- **計画ギャップ**: SC-07 の前提「変換ジョブのバックエンド API は実装済み」は成立していなかった（ConversionService は照会 API 無し）。計画側 05_screens/03_usecases（UC-06）に「変換ジョブ照会・再変換 API」を明示する提案を環流する。
- **UC-06 の意味的差分**: 計画 UC-06 の代替フローは「変換結果を管理者が補正して再登録する」（内容の**手編集**＋再登録）だが、本 MVP は人手補正を**再変換（原本イベントの再発行）に限定**した（内容手編集 UI は未実装）。この解釈差分も UC-06 へ環流し、手編集フローの要否を計画側で判断してもらう（レビュー #172 指摘対応）。
- **永続化 follow-up**: インメモリ→Postgres+EF（DataSourceService 準拠）へ差し替え、再起動耐性・複数インスタンス共有・監査保存を得る。別 issue 化する。
