---
title: 作業仕様書 — 構成情報 API・イントロスペクション・ドリフト検出（FR-15）
type: spec
status: in-progress
related_ids:
  - FR-15
  - ADR-0018
author: claude
created: 2026-07-07
updated: 2026-07-07
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ./20260708_issue-111_declarative-pipeline-config.md
  - ../adr/IADR-0027_composability-folder-structure.md
  - ../adr/IADR-0028_declarative-pipeline-config.md
  - ../adr/IADR-0029_config-info-api-placement-and-drift-granularity.md
---

# 作業仕様書: 構成情報 API・イントロスペクション・ドリフト検出

Issue: #112（親: #102 ／ 依存: #111 宣言的パイプライン構成）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-15（実効構成の読み取り専用取得・宣言との突合・閲覧の管理者/運用者限定）
- 関連 ADR: ADR-0018（宣言的構成＋プラグイン規約、Accepted）
- 計画書リンク: `06_technical/10_composability-design.md` §設計要素 6（構成情報 API・イントロスペクション）

## 目的・背景

組み替えが自由になるほど「いま何がどう繋がっているか」は自明でなくなる（FR-14 と一対）。運用・障害調査・監査の
ため、現在有効な実効構成（段・接続・ポート選択・コネクタ・構成バージョン）を機械可読で取得でき、宣言（Git・
#111 の pipeline.json）と実効の不一致（ドリフト）を検出・警告できる仕組みを実装する。閲覧は管理者・運用者に限定する。

## 対象範囲

- 対象:
  1. **自己申告（イントロスペクション）**: Shared の再利用可能コンポーネント。段は `IPipelineStep`＋
     `IConsumer<TIn>` の型情報と宣言から実効値を導出し、選択中ポート・コネクタと併せて申告する。エンドポイントは
     `GET /internal/introspection`（メッシュ内部限定・ingress 非公開・OpenAPI 非掲載）。
  2. **構成情報 API（読み取り専用）**: BFF 配下の管理 API。`GET /bff/admin/config`（実効構成）・
     `GET /bff/admin/config/drift`（ドリフト）。自己申告を集約し、宣言＋構成バージョンと合わせて応答を組み立てる。
  3. **ドリフト検出**: 宣言（pipeline.json）と実効の突合。定期（既定 5 分）＋取得時。不一致は運用アラート経路
     （構造化ログ `ConfigDrift=true`）へ警告。
  4. **アクセス制御・監査**: `ConfigViewer` ポリシー（`platform-admin` / `platform-operator`）に限定。非権限は
     404 で存在秘匿。取得操作（許可・拒否）を監査ログ（`Audit=true`）へ記録。
  5. **記録**: 設計判断を IADR-0029 に起票。
- 対象外:
  - 画面表示（構成ビューア SC-11 は別 issue）。
  - バックグラウンドワーカー（conversion / ingestion）への自己申告配線・全 HTTP サービスへの横展開・Helm への
    GitOps 注入（IADR-0029 のフォローアップ。未配線サービスの宣言段は Unverifiable 扱いで誤検知にしない）。

## 設計

### 1. 自己申告（Shared.Infrastructure/Foundation/Introspection/）

- `ServiceIntrospectionDto`（Shared.Contracts）: `Service` / `Steps[]` / `Ports[]` / `Connectors[]`。
- `AddKnowledgePlatformIntrospection(service, pipeline, configure)`: `IntrospectionBuilder` で段（`AddStep<T>()`）・
  ポート（`AddPort`）・コネクタ（`AddConnector`）を宣言的に組み立て、singleton 登録。段の実効値
  （enabled・outputs）は IADR-0028 の登録規則と同じく宣言から導出。
- `MapKnowledgePlatformIntrospection()`: `GET /internal/introspection` を map（メッシュ内部限定・`ExcludeFromDescription`）。
- 本 PR の配線: document-service（catalog）・wiki-service（wiki-sync / wiki-delete）。

### 2. 集約と実効構成の組み立て

- `IEffectiveConfigCollector`（`HttpEffectiveConfigCollector`）: `Introspection:Services`（service 名→URL）の
  自己申告を HTTP 収集。到達不能サービスは `UnreachableServices` に記録（誤検知抑制の材料）。
- `ConfigInspectionService`:
  - `GetEffectiveConfigAsync()` → `EffectiveConfigDto`（段一覧・イベント接続・ポート選択・コネクタ・構成バージョン）。
    段は宣言順にソート。イベント接続は宣言 sources＋段の入出力から発行者/購読者を算出。
  - `GetDriftAsync()` → `DriftReportDto`。
  - 構成バージョンは `Config:GitCommit/AppliedAt/AppliedBy`（GitOps 注入）から。

### 3. ドリフト検出（`DriftDetector` 純粋関数）

| 種別 | 条件 | 深刻度 |
| --- | --- | --- |
| MissingApply | 宣言で有効・担当サービス到達可能・実効に無い（適用漏れ・起動失敗） | Warning |
| UndeclaredSubscription | 実効で有効・宣言に無い（宣言に無い購読） | Warning |
| StaleStage | 宣言で無効・実効で稼働（古い段の残留） | Warning |
| BindingMismatch | 段名一致・input/consumer 不一致 | Warning |
| Unverifiable | 宣言で有効・担当サービス到達不能（適用漏れと断定せず保留） | Info |

- キュー名の相違は判定に含めない（GitOps 既定命名差の誤検知抑制）。
- `DriftDetectionHostedService`（BFF ホスト）が既定 5 分間隔で検出し、`IDriftAlertSink` で警告。`Drift:Enabled=false`
  で無効化（テスト・ローカル）。

### 4. アクセス制御・監査

- `KnowledgePlatformAuthPolicies.ConfigViewer`（`platform-admin` / `platform-operator`）を追加。
- 構成情報エンドポイントは `RequireAuthorization()`（認証必須）＋ハンドラ内でロール検査。非権限は 404（存在秘匿）。
- `IAuditLogger`（`AuditLogger`）で取得操作を許可・拒否とも記録。

### 5. 変更対象ファイル（主要）

| 区分 | ファイル |
| --- | --- |
| 契約 | `Shared.Contracts/Dtos/ConfigInfoDto.cs`（新設） |
| 基盤 | `Shared.Infrastructure/Foundation/Introspection/*`（新設）・`Foundation/Audit/AuditLogger.cs`（新設） |
| 基盤 | `Foundation/Pipeline/PipelineOptions.cs`（events/sources 追加）・`Foundation/Extensions/AuthExtensions.cs`（ConfigViewer） |
| BFF | `Foundation/Endpoints/ConfigBffEndpoints.cs`（新設）・`Program.cs`・`appsettings.json` |
| 段 | document / wiki の `Program.cs`（自己申告配線） |
| テスト | `Bff.Tests/DriftDetectorTests.cs`・`Bff.Tests/ConfigBffEndpointTests.cs`・`Bff.Tests/BffTestFactory.cs` |
| 文書 | 本仕様書・IADR-0029・`docs/adr/README.md` |

## 受け入れ基準

- [x] 各（段ホスト）サービスが自己申告エンドポイントを持ち、メッシュ内部限定である（`/internal/introspection`・
      ingress 非公開・OpenAPI 非掲載。document / wiki に配線。横展開は IADR-0029 フォローアップ）
- [x] 構成情報 API が実効構成（段・接続・ポート選択・コネクタ・構成バージョン）を読み取り専用で返す
      （`ConfigBffEndpointTests.GetConfig_AsAdmin_ReturnsEffectiveConfigWithVersion`）
- [x] 宣言と実効の不一致が検出され、警告経路が発火する（適用漏れ・宣言に無い購読のテスト:
      `DriftDetectorTests`・`ConfigBffEndpointTests.GetDrift_WhenUndeclaredSubscription_ReportsDrift`）
- [x] 管理者・運用者以外のアクセスは 404 で応答を秘匿し、取得操作が監査ログに残る
      （`GetConfig_AsNonPrivileged_Returns404AndAuditsDenied`・`GetConfig_AsAdmin_AuditsGranted`）
- [x] 実装配置・申告規約・ドリフト判定粒度の判断が IADR-0029 に記録されている
- [ ] ビルド・テスト・lint が CI で成功する（サンドボックス制約で dotnet はローカル未実走。CI で確認）

## テスト方針

- 単体（`DriftDetectorTests`）: 一致・適用漏れ・宣言に無い購読・検証不能・古い段の残留・バインディング不一致。
- エンドポイント（`ConfigBffEndpointTests`）: 実効構成の取得・運用者ロール許可・非権限 404＋監査 denied・
  監査 granted・ドリフト検出。自己申告収集はスタブ（`IEffectiveConfigCollector`）で制御。
- ビルド/テストはサンドボックス制約でローカル未実走のため CI（ci.yml）で確認する（#111 と同様）。

## 計画書との差異

- 差異なし。計画の未決事項（実装配置・申告規約・ドリフト判定粒度）を IADR-0029 で確定した。

## 未決事項（フォローアップ）

- ワーカー（conversion / ingestion）の自己申告エンドポイント・全 HTTP サービスへの横展開・Helm GitOps 注入。
- 適用直後のドリフト即時検出（ArgoCD PostSync 起動）。
