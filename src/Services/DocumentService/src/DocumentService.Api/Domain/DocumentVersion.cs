namespace DocumentService.Api.Domain;

// FR-06, UC-03: 文書の確定版スナップショット（append-only）。
// 任意時点のタイトル・状態・本文 URI・メタデータを ID＋版番号で再構成するために保持する。
public class DocumentVersion
{
    // FR-06: 版の主キーは EF（ValueGeneratedOnAdd）に採番させる。
    // フィールド初期化で非デフォルト値を入れると、追跡済み Document への追記時に
    // EF が「既存行」と誤認して UPDATE を発行し DbUpdateConcurrencyException になるため設定しない。
    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public int Version { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Status { get; private set; } = DocumentStatus.Draft;
    public string? MarkdownUri { get; private set; }
    public Dictionary<string, string> Attributes { get; private set; } = [];
    public List<string> Tags { get; private set; } = [];
    public string? ChangeNote { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private DocumentVersion() { }

    // FR-06: 文書の現在状態をスナップショットとして切り出す。メタデータは防御的にコピーする。
    internal static DocumentVersion Capture(Document doc, string? changeNote) => new()
    {
        DocumentId = doc.Id,
        Version = doc.Version,
        Title = doc.Title,
        Status = doc.Status,
        MarkdownUri = doc.MarkdownUri,
        Attributes = new Dictionary<string, string>(doc.Attributes),
        Tags = [.. doc.Tags],
        ChangeNote = changeNote,
        CreatedAt = doc.UpdatedAt,
    };
}
