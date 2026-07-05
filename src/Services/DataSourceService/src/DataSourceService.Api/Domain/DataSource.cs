namespace DataSourceService.Api.Domain;

// FR-01, UC-04: データソースエンティティ
public class DataSource
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = string.Empty;
    public string SourceType { get; private set; } = string.Empty; // filesystem|wiki|saas|db
    public string ConnectionUri { get; private set; } = string.Empty;
    public string Status { get; private set; } = DataSourceStatus.Active;
    public DateTimeOffset? LastSyncedAt { get; private set; }
    public Dictionary<string, string> Config { get; private set; } = [];

    // FR-01, FR-05, ADR-0004: このデータソース由来の原本へ既定で付与する ABAC 文書属性。
    // 取り込み時に RawDocumentFetched.Attributes へ写像され、下流の fail-closed 検索（IADR-0012）で
    // 文書が機密区分（confidentiality）欠落により除外されるのを防ぐ。
    public Dictionary<string, string> DefaultAttributes { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private DataSource() { }

    // FR-05, ADR-0004: 機密区分の許可値は AuthorizationService の属性辞書に準拠（public|internal|confidential|restricted）。
    // データソース登録時に既定機密区分が未指定の場合のフェイルセーフ既定値。public（過剰公開）でも
    // restricted（過剰制限）でもなく、社内文書の基準となる internal を採る。
    public const string ConfidentialityKey = "confidentiality";
    public const string DefaultConfidentiality = "internal";

    public static DataSource Create(string name, string sourceType, string connectionUri,
        Dictionary<string, string>? config = null,
        Dictionary<string, string>? defaultAttributes = null)
    {
        // FR-01, FR-05: 原本には機密区分を必ず付与する。未指定・空はフェイルセーフ既定値で補う。
        var attributes = defaultAttributes is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(defaultAttributes);
        if (!attributes.TryGetValue(ConfidentialityKey, out var conf) || string.IsNullOrWhiteSpace(conf))
            attributes[ConfidentialityKey] = DefaultConfidentiality;

        return new()
        {
            Name = name,
            SourceType = sourceType,
            ConnectionUri = connectionUri,
            Config = config ?? [],
            DefaultAttributes = attributes,
        };
    }

    public void RecordSync()
    {
        LastSyncedAt = DateTimeOffset.UtcNow;
    }

    public void Disable() => Status = DataSourceStatus.Disabled;
}

public static class DataSourceStatus
{
    public const string Active = "active";
    public const string Disabled = "disabled";
}
