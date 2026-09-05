namespace Knowledge.Contracts.Dtos;

public class DocumentDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = "draft";
    public string? MarkdownUri { get; init; }
    // FR-06, UC-03: 現在の版番号
    public int Version { get; init; } = 1;
    public Dictionary<string, string> Attributes { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    // SC-03, ADR-0070 決定 3・決定 4 / [[IADR-0388]] 決定 2 (#1254): **原本が本文を持っていたか。**
    // `false` は「本文なしで完了した」（テキスト層を持たない PDF）で、文書詳細は本文の位置へ
    // 「本文なし（原本を参照）」を出す（**SC-02 と同じ文言・同じ導出**）。
    // **既定は `true`**（本項目を持たない旧応答は従来どおり「本文あり」として読める）。
    public bool HasBody { get; init; } = true;
}

// SC-03, FR-06: 文書本文（正規化 Markdown）の取得結果。BFF が ABAC 判定後にオブジェクト
// ストレージ（storage://）から読み取り、閲覧を許可された呼び出し元にのみ払い出す。
public record DocumentContentDto(Guid Id, string Title, string Markdown, string? SourceUri);

// FR-06, UC-03: 文書の版スナップショット（**メタデータのみ**）。
//
// 🔴 **本文の参照（`MarkdownUri`）は持たない**（#1011 / IADR-0290）。本文のオブジェクトキーは
// 文書 ID だけから決まる固定キー（`DocumentBodyIntake.StorageKey`）で、再投入は同じキーを上書きし、
// 参照 URI（`storage://<bucket>/<key>`）は versionId を持たない ——
// **「その版の本文」を指せる値が存在しない**。以前はここに `MarkdownUri` があり、
// 現行版の本文 URI がそのまま入っていたため、**呼び出し側が過去版の本文だと読み違えても
// 応答からは区別できなかった**。契約の側を事実へ揃えて落とした。
//
// 版の**復元**は FR-06 の射程外である（計画 FR-06［2026-08-23 明確化］・環流 planning#473）。
// 射程は版の作成・一覧・取得までであり、本 DTO は取得の応答である。
public class DocumentVersionDto
{
    public Guid DocumentId { get; init; }
    public int Version { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = "draft";
    public Dictionary<string, string> Attributes { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public string? ChangeNote { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
