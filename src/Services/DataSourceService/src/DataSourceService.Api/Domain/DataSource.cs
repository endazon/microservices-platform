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
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private DataSource() { }

    public static DataSource Create(string name, string sourceType, string connectionUri,
        Dictionary<string, string>? config = null)
        => new()
        {
            Name = name,
            SourceType = sourceType,
            ConnectionUri = connectionUri,
            Config = config ?? [],
        };

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
