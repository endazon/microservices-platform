---
title: IADR-0029 構成情報 API は BFF 配下の管理 API へ同居させ、自己申告集約＋宣言突合でドリフトを検出する
type: impl-adr
status: Accepted
related_ids:
  - FR-15
  - ADR-0018
author: claude
created: 2026-07-07
updated: 2026-07-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
  - planning:projects/microservices-platform/06_technical/10_composability-design.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# IADR-0029: 構成情報 API の実装配置・申告規約・ドリフト判定粒度

- 状態: Accepted
- 日付: 2026-07-07
- 決定者: claude（issue #112 実装）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: FR-15・ADR-0018
- 関連する実装仕様書: [作業仕様書](../specs/20260707_FR-15_config-info-api-introspection-drift.md)・
  [IADR-0027](./IADR-0027_composability-folder-structure.md)・[IADR-0028](./IADR-0028_declarative-pipeline-config.md)・
  [IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md)（存在秘匿の先例）

## コンテキストと課題

ADR-0018 と 10_composability-design.md §6 は、組み替え自由化（FR-14）と一対で「現在有効な実効構成を
機械可読に取得し、宣言（Git）との不一致（ドリフト）を検出・警告する」ことを必須とした。計画は実装リポジトリへ
以下の設計を委ねた（10_composability-design.md 末尾の未決事項）。

1. **実装配置**: どのサービスが構成情報 API を担うか（独立サービス化はしない＝過剰分割回避）。
2. **自己申告（イントロスペクション）の規約**: 各サービス・段がどう自身の構成を申告するか、到達範囲。
3. **ドリフト判定粒度**: バインディングの完全一致か、宣言に無い購読のみ警告か。誤検知の抑制。

## 検討した選択肢

### 実装配置

1. **BFF 配下の管理 API（`/bff/admin/config`）へ同居（採用）**。BFF は既にエッジ認証（Keycloak JWT・
   ロール）と各サービスへの集約経路を持ち、構成情報の閲覧制御（管理者・運用者限定）と自然に整合する。
2. 既存のいずれかのドメインサービスへ同居。ドメイン責務と無関係な横断機能を混入させ、凝集を下げる。
3. 独立した構成情報サービスを新設。計画が明示的に禁じる過剰分割（独立サービス化はしない）。

### 自己申告の規約

1. **Shared の再利用可能コンポーネント `GET /internal/introspection`（採用）**。段は
   `IPipelineStep`＋`IConsumer<TIn>` の型情報から段名・購読イベント・consumer 完全名を、宣言（pipeline.json）
   から有効状態・出力を導出して申告する。ポート実装・コネクタは合成ルート（`Program.cs`）で明示登録する。
   到達範囲はメッシュ内部限定（ingress へ公開しない。IADR-0017 ネットワーク分離／IADR-0026 mTLS が防御）。
2. 各サービスがアセンブリスキャンで動的申告。型安全性が下がり、IADR-0028（fail-fast の型照合）と二重管理になる。

### ドリフト判定粒度

1. **段の存在・有効状態・購読バインディング（input/consumer）を突合。キュー名差は情報レベル（採用）**。
   種別を「適用漏れ（MissingApply）／宣言に無い購読（UndeclaredSubscription）／古い段の残留（StaleStage）／
   バインディング不一致（BindingMismatch）／検証不能（Unverifiable）」に分類する。担当サービスが到達不能な
   場合は適用漏れと断定せず Unverifiable に留めて誤検知を抑制する。
2. 完全一致（キュー名・順序まで）を要求。GitOps の正当な既定命名差でも警告が出て誤検知が増える。
3. 宣言に無い購読のみ警告。適用漏れ（本番でメッセージを取りこぼす最重要ケース）を見逃す。

## 決定

- **実装配置**: 構成情報 API は **BFF 配下の管理 API**（`/bff/admin/config`・`/bff/admin/config/drift`）とする。
  独立サービス化しない。実効構成の集約・ドリフト定期検出・監査は Shared の再利用可能コンポーネントとして実装し、
  BFF がホストする。
- **自己申告**: Shared の `AddKnowledgePlatformIntrospection` / `MapKnowledgePlatformIntrospection` で
  `GET /internal/introspection`（メッシュ内部限定・ingress 非公開・OpenAPI 非掲載）を提供する。応答は
  `ServiceIntrospectionDto`（段・選択中ポート・コネクタ）。段の実効値は IADR-0028 の登録規則と同じ導出で申告する。
- **集約**: `IEffectiveConfigCollector`（HTTP 実装）が設定済みサービス（`Introspection:Services`）の自己申告を
  収集し、`ConfigInspectionService` が実効構成 DTO（段・イベント接続・ポート選択・コネクタ・構成バージョン）へ
  組み立てる。構成バージョンは `Config:GitCommit/AppliedAt/AppliedBy`（GitOps 注入）から取得する。
- **ドリフト検出**: `DriftDetector`（純粋関数）が宣言（pipeline.json）と実効を突合し、上記 5 種別を返す。
  `DriftDetectionHostedService` が既定 5 分間隔（`Drift:IntervalSeconds`）で検出し、不一致を `IDriftAlertSink`
  （既定は構造化ログ Warning、`ConfigDrift=true`）で運用アラート経路（05_observability-ops）へ通知する。適用直後の
  即時検出は `/bff/admin/config/drift` の取得または RunOnce で補完する。
- **アクセス制御・監査**: 閲覧は `ConfigViewer` ポリシー（管理者 `platform-admin` または運用者
  `platform-operator`）に限定する。**非権限者には 404 を返して応答自体を秘匿**する（IADR-0009 の存在秘匿と整合）。
  取得操作は許可・拒否とも `IAuditLogger`（`Audit=true`）で監査ログへ記録する。

## 理由

- BFF 同居は過剰分割を避けつつ、既存のエッジ認証・ロール・集約経路を再利用でき、閲覧制御と監査の実装が最短で済む。
- 自己申告を Shared の 1 行 API にすることで、段ホストサービスへの導入コストを下げ、IADR-0027/0028 の型情報を
  そのまま実効構成の根拠に使える（宣言と実装の二重管理を増やさない）。
- ドリフト種別に「検証不能」を設けたことが誤検知抑制の要。到達不能（デプロイ中・一時障害）を適用漏れと誤警告すると
  アラート疲れを招くため、両者を明確に区別する。キュー名差を情報レベルに留めるのも同じ理由。

## 結果

- 良い影響: 実効構成とドリフトが読み取り専用 API で取得でき、FR-15 の受け入れ基準（実効構成の取得・適用漏れ/
  宣言に無い購読の検出・存在秘匿・監査）を満たす。SC-11 構成ビューア（別 issue）の API 基盤が確定する。
- 悪い影響・トレードオフ: 自己申告エンドポイントは当面 HTTP サービス（本 PR では document / wiki の段ホスト）へ配線し、
  バックグラウンドワーカー（conversion / ingestion）への配線と全 HTTP サービスへの横展開・Helm への
  `Introspection:Services` / `Config:*` 注入はフォローアップとする。未配線サービスの宣言段は Unverifiable として
  扱われ、適用漏れの誤検知にはならない。
- フォローアップ:
  1. conversion / ingestion ワーカーへメッシュ内部限定の自己申告エンドポイントを追加する（最小 HTTP サーフェス）。
  2. 全 HTTP サービスへ `MapKnowledgePlatformIntrospection` を横展開し、ポート実装・コネクタの申告を拡充する。
  3. Helm values / deployment に `Introspection:Services`・`Config:GitCommit` 等を GitOps 注入する。
  4. 適用直後のドリフト即時検出を ArgoCD PostSync フック等から起動する。

## 関連

- Supersedes: なし
- Superseded by: なし
