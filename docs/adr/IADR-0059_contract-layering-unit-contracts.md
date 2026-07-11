---
title: IADR-0059 契約を階層化し、ナレッジ固有のイベント契約を Knowledge.Contracts へ分離する（URN は新名前空間から導出・後方互換なし。#227/IADR-0062 で URN 固定を撤回）
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - ADR-0018
  - IADR-0027
  - IADR-0056
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-14: 構成変更で完結する疎結合ユニット)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018 (契約・イベントによる疎結合)"
---

# IADR-0059: 契約の階層化とナレッジ固有イベント契約の Knowledge.Contracts 分離

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-14（構成変更のみで完結する疎結合ユニット）
- 関連 ADR: ADR-0018（契約・イベント疎結合）／[[IADR-0027]]（固定/可変の名前空間規約）／[[IADR-0056]]（ユニット第一構成）
- 関連仕様書: `docs/specs/20260711_issue-229_knowledge-contracts-separation.md`、[`src/README.md`](../../src/README.md)（依存規則）
- Issue: #229（IADR-0056 フォローアップ 3）

## コンテキストと課題

再編（#210 / IADR-0056）で platform ユニットを基盤として分離したが、**ナレッジドメイン固有の契約が platform の契約プロジェクト（`platform/backend/Shared/KnowledgePlatform.Shared.Contracts`）に同居**している。

1. **イベント契約**: `RawDocumentFetched` / `DocumentNormalized` / `DocumentUpdated` / `DocumentDeleted` / `IngestionRequested` / `IngestionCompleted` の 6 イベントは knowledge ドメインのものだが platform の契約に置かれている。別の可変機能ユニットを追加した場合、そのユニットの契約をどこに置くかの規約がない。
2. **DTO・BFF 集約**: 多くの DTO（`DocumentDto` / `SearchDto` 等）も knowledge 固有で、BFF（platform）にはナレッジ固有の集約ロジックが実装されている。

本 IADR は **契約の階層化方針**を定め、その第 1 スライスとして**イベント契約の分離**を決定する。DTO 移設と BFF 合成は本方針に沿った後続スライスとする。

> **更新（2026-07-11・#227 / [[IADR-0062]]）**: 本 IADR の当初決定にあった「`[MessageUrn]` で旧 URN（`KnowledgePlatform.Shared.Contracts.Events:*`）に固定して wire 後方互換を維持する」という方針は**撤回した**。今回の再編では**後方互換を持たせない**方針に変更し、`[MessageUrn]` 属性を削除して **URN はイベント型の現名前空間 `Knowledge.Contracts.Events` から導出する正準値**（`urn:message:Knowledge.Contracts.Events:*`）へ統一した。旧 URN は非互換・削除。以降の記述はこの方針で読む。

### MassTransit のメッセージ URN（正準値）

イベントは MassTransit（RabbitMQ）で流れる。MassTransit は既定でメッセージ URN を **.NET 型の名前空間＋型名**から導出する（`urn:message:{namespace}:{typename}`）。イベントは `Knowledge.Contracts.Events` に置くため、URN は `urn:message:Knowledge.Contracts.Events:<Name>` を正準値とする。送受信は同一の型定義（`Knowledge.Contracts`）を共有するため URN は自ずと一致する。**後方互換（旧 URN との互換）は要求しない**（上記 更新）。

## 検討した選択肢

**契約の配置**:
1. 現状維持（platform の Shared.Contracts に同居）: 可変ユニット追加時に platform 契約を都度改修する必要があり FR-14 の疎結合に反する。
2. **契約の階層化（本決定）**: platform 共通契約（横断・エンベロープ）は `platform/backend/Shared/KnowledgePlatform.Shared.Contracts` に残し、**ユニット固有契約は `<unit>.Contracts`（本件は `Knowledge.Contracts`、knowledge ユニット内）へ分離**する。ユニット間はイベント**名**の宣言的バインディング（`pipeline.json`）で疎結合を保ち、.NET 型の所在は wire 契約（URN）から切り離す。

**イベント移設時の URN の扱い**（当初の検討。後方互換方針は上記 更新で撤回済み）:
1. 名前空間を据え置いたまま物理プロジェクトだけ移す: 「名前空間＝フォルダ階層」（IADR-0027）に反する。
2. 新名前空間へ移し `[MessageUrn]` で旧 URN を固定する（当初決定）: 後方互換を得るが、旧ブランドの URN を恒久的に抱える。
3. **新名前空間へ移し URN も新名前空間から導出する（現方針・#227/IADR-0062）**: `[MessageUrn]` を付けず、URN を `urn:message:Knowledge.Contracts.Events:<Name>` に統一する。後方互換は持たせない（旧 URN 削除）。IADR-0027 に整合し、命名が URN と一貫する。

## 決定

**契約を 2 層に階層化する。**

- **platform 共通契約**（`KnowledgePlatform.Shared.Contracts`）: ユニット横断で共有する契約（横断 DTO・将来の共通エンベロープ）。
- **ユニット固有契約**（`Knowledge.Contracts` = `src/knowledge/backend/Shared/Knowledge.Contracts`）: そのユニットのドメイン契約。**本スライスで 6 イベントを移設**する。

**イベントは新名前空間 `Knowledge.Contracts.Events` に置く。URN は当該名前空間から導出する正準値（`urn:message:Knowledge.Contracts.Events:<Name>`）に統一し、`[MessageUrn]` による旧 URN 固定は行わない**（後方互換なし・#227/IADR-0062 の方針）。ユニット間のイベント連携は従来どおり `pipeline.json` のイベント**名**（宣言的バインディング）で行い、型の所在（どのユニット/プロジェクトか）に依存しない。

**本スライスの範囲外（後続スライス）**: knowledge 固有 DTO の `Knowledge.Contracts` 移設と、BFF のユニット別エンドポイント合成方式。これらは platform→可変ユニットの依存禁止（[[IADR-0056]] / `src/README.md`）に触れるため、BFF が knowledge DTO へ依存しない**合成点**の設計とセットで行う必要があり、独立スライスとする（#229 に follow-up として残す）。

## 理由

- **FR-14 整合**: ユニット固有契約をユニット内に閉じることで、可変ユニット追加時に platform 契約の改修を不要にする方向へ進む（本スライスはイベントを対象）。
- **命名と URN の一貫**: URN をイベント型の名前空間から導出することで、命名（`Knowledge.Contracts.Events`）と wire URN が一致する。後方互換は要求しない方針のため旧 URN 固定は行わず、正準 URN を回帰テストで固定する。
- **依存方向を壊さない**: 6 イベントは knowledge サービスのみが購読/発行し、BFF はイベント**名**（文字列・pipeline.json）でのみ扱い型参照しない。よってイベント移設は platform→knowledge 依存を生まず、[[IADR-0057]] の依存方向検査を通る。
- **段階実施**: DTO/BFF 合成は依存禁止に触れる大きな設計を要するため分離し、過剰実装・広範な競合を避ける（#227 名前空間改名とも順序調整）。

## 結果

- 新規: `src/knowledge/backend/Shared/Knowledge.Contracts/`（`Knowledge.Contracts.csproj` + `Events/*.cs`、`backend.slnx` 登録）。
- 6 イベントを `Knowledge.Contracts.Events` へ移設。platform `Shared.Contracts/Events/` は削除。URN は名前空間から導出（`[MessageUrn]` は付けない。#227/IADR-0062 で旧 URN 固定を撤廃）。
- knowledge の 5 サービス（Conversion/DataSource/Document/Ingestion/Wiki）が `Knowledge.Contracts` を参照、`using` を更新。
- 回帰テスト `Knowledge.Contracts.Tests`（6 イベントの `MessageUrn` が正準値 `urn:message:Knowledge.Contracts.Events:*` であることを固定）。
- platform 側の契約・BFF は無改修（イベント型を参照しないため）。

## フォローアップ（#229 継続）

- knowledge 固有 DTO の `Knowledge.Contracts` 移設（BFF の knowledge DTO 依存を解消する合成点とセット）。
- BFF のユニット別エンドポイント合成方式（[[IADR-0027]] の合成ルート概念の BFF 版）。
- #227（名前空間・アセンブリ改名）で `[MessageUrn]` を削除し、URN を `Knowledge.Contracts.Events` から導出する正準値へ統一済み（後方互換なし・[[IADR-0062]]）。

## 関連

- Supersedes: なし
- Superseded by: なし
