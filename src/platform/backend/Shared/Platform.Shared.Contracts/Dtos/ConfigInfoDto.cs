namespace Platform.Shared.Contracts.Dtos;

// FR-15, ADR-0018: 構成情報 API・イントロスペクション・ドリフト検出で用いる契約 DTO 群。
// 自己申告（各サービス・段）と、集約された実効構成・ドリフト結果を表現する。

// 各サービス・段の自己申告（イントロスペクション）。メッシュ内部限定のエンドポイントが返す。
public sealed record ServiceIntrospectionDto(
    string Service,
    IReadOnlyList<StepIntrospectionDto> Steps,
    IReadOnlyList<PortSelectionDto> Ports,
    IReadOnlyList<ConnectorDto> Connectors);

// パイプライン段の実効値（宣言と照合された購読/発行・有効状態）。
public sealed record StepIntrospectionDto(
    string Name,
    string Consumer,
    string Input,
    IReadOnlyList<string> Outputs,
    bool Enabled);

// ポート（LLM・埋め込み・ベクトルDB・ストレージ・Wiki 同期等）で選択中の実装と接続先識別子。
public sealed record PortSelectionDto(string Port, string Implementation, string? Target);

// データソースコネクタと有効・無効状態（09_datasource-connectors の管理情報を集約）。
public sealed record ConnectorDto(string Name, bool Enabled);

// FR-15: 構成情報 API の応答（実効構成）。
public sealed record EffectiveConfigDto(
    ConfigVersionDto Version,
    IReadOnlyList<PipelineStageDto> Pipeline,
    IReadOnlyList<EventBindingDto> EventBindings,
    IReadOnlyList<PortSelectionDto> Ports,
    IReadOnlyList<ConnectorDto> Connectors);

// 構成バージョン（適用中の構成定義の Git コミット ID・適用日時・適用者）。
public sealed record ConfigVersionDto(string? GitCommit, DateTimeOffset? AppliedAt, string? AppliedBy);

// FR-15 (#139), IADR-0046: 構成バージョン履歴の 1 エントリ。正データ源は GitOps 層（Git/ArgoCD の適用履歴）で、
// API は永続化せず注入されたスライスを返す。HadDrift はその時点のドリフト有無（注入時に判明していれば設定、
// 不明なら null＝「—」表示）。
public sealed record ConfigVersionEntryDto(
    string? GitCommit,
    DateTimeOffset? AppliedAt,
    string? AppliedBy,
    bool? HadDrift);

// 有効な段（順序・入出力イベント型・所属サービス）。
public sealed record PipelineStageDto(
    string Name,
    string Service,
    string Consumer,
    string Input,
    IReadOnlyList<string> Outputs,
    bool Enabled);

// イベント接続（イベント型ごとの発行者・購読者）。
public sealed record EventBindingDto(
    string Event,
    IReadOnlyList<string> Publishers,
    IReadOnlyList<string> Subscribers);

// FR-15: 宣言（Git）と実効構成のドリフト検出結果。
public sealed record DriftReportDto(
    bool HasDrift,
    DateTimeOffset CheckedAt,
    IReadOnlyList<DriftFindingDto> Findings);

// 個々の不一致（Kind: 種別、Severity: 深刻度、Target: 対象、Detail: 説明）。
public sealed record DriftFindingDto(string Kind, string Severity, string Target, string Detail);
