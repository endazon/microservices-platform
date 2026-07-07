namespace DocumentService.Api.Domain;

// FR-06, ADR-0002: 正規化文書エンティティ（DB per Service）
public class Document
{
    private readonly List<DocumentVersion> _versions = [];

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Title { get; private set; } = string.Empty;
    public string Status { get; private set; } = DocumentStatus.Draft;
    public string? MarkdownUri { get; private set; }
    public string? OriginalUri { get; private set; }
    public string? ContentType { get; private set; }
    public int Version { get; private set; } = 1;
    public Dictionary<string, string> Attributes { get; private set; } = [];
    public List<string> Tags { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    // FR-06, UC-03: 版履歴（append-only）。作成・各更新で確定版のスナップショットを追記する。
    public IReadOnlyList<DocumentVersion> Versions => _versions;

    private Document() { }

    public static Document Create(string title, string? originalUri, string? contentType,
        Dictionary<string, string>? attributes = null, List<string>? tags = null)
    {
        var doc = new Document
        {
            Title = title,
            OriginalUri = originalUri,
            ContentType = contentType,
            Attributes = attributes ?? [],
            Tags = tags ?? [],
        };
        // FR-06: 作成時点を版 1 として記録する。
        doc.Snapshot("created");
        return doc;
    }

    // FR-01, UC-04: 正規化文書（DocumentNormalized）からカタログ文書を生成する。
    // パイプライン全体で ID を一貫させるため、変換側が採番した DocumentId を指定する（IADR-0001）。
    public static Document CreateNormalized(Guid id, string title, string markdownUri,
        Dictionary<string, string>? attributes = null, List<string>? tags = null)
    {
        var doc = new Document
        {
            Id = id,
            Title = title,
            MarkdownUri = markdownUri,
            Status = DocumentStatus.Normalized,
            Attributes = attributes ?? [],
            Tags = tags ?? [],
        };
        doc.Snapshot("normalized");
        return doc;
    }

    public void Update(string title, Dictionary<string, string> attributes, List<string> tags,
        string? changeNote = null)
    {
        Title = title;
        Attributes = attributes;
        Tags = tags;
        Touch();
        Snapshot(changeNote ?? "updated");
    }

    // FR-06, UC-03: メタデータ（属性・タグ）のみを更新する。本文・タイトルは変更しない。
    public void UpdateMetadata(Dictionary<string, string> attributes, List<string> tags,
        string? changeNote = null)
    {
        Attributes = attributes;
        Tags = tags;
        Touch();
        Snapshot(changeNote ?? "metadata-updated");
    }

    // FR-01, UC-04: 同一文書の DocumentNormalized 再配信時に正規化内容を反映する（冪等更新）。
    public void ApplyNormalized(string title, string markdownUri,
        Dictionary<string, string> attributes, List<string> tags)
    {
        Title = title;
        MarkdownUri = markdownUri;
        Status = DocumentStatus.Normalized;
        Attributes = attributes;
        Tags = tags;
        Touch();
        Snapshot("re-normalized");
    }

    public void SetMarkdownUri(string uri)
    {
        MarkdownUri = uri;
        Status = DocumentStatus.Normalized;
        Touch();
        Snapshot("markdown-set");
    }

    public void Publish()
    {
        Status = DocumentStatus.Published;
        Touch();
        Snapshot("published");
    }

    // FR-06, UC-03, Issue #88: 文書をアーカイブ（非公開化）する。下流（Wiki.js 同期）は
    // status=archived の DocumentUpdated を受けてページを非公開化する。
    public void Archive()
    {
        Status = DocumentStatus.Archived;
        Touch();
        Snapshot("archived");
    }

    private void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }

    // FR-06: 現在の状態を版スナップショットとして履歴へ追記する。
    private void Snapshot(string? changeNote)
        => _versions.Add(DocumentVersion.Capture(this, changeNote));
}

public static class DocumentStatus
{
    public const string Draft = "draft";
    public const string Normalized = "normalized";
    public const string Published = "published";
    // Issue #88: アーカイブ（非公開化）。削除と異なり実体は保持し、閲覧経路から不可視にする。
    public const string Archived = "archived";
}
