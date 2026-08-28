namespace DocumentService.Domain;

// FR-06, UC-03: 文書の確定版スナップショット（append-only）。
// 任意時点のタイトル・状態・メタデータを ID＋版番号で再構成するために保持する。
//
// 🔴 **本文そのものは版ごとに保持しない**（#1011 / [[IADR-0290]]）。再構成できるのは
// **メタデータだけ**である。本文のオブジェクトキーは文書 ID で固定（`DocumentBodyIntake.StorageKey`）で
// 再投入が上書きし、参照 URI は versionId を持たないため、過去版の本文を指す値が存在しない。
// これは計画と整合する —— FR-06 の射程は版の作成・一覧・取得まで（計画 FR-06［2026-08-23 明確化］・
// 環流 planning#473）。
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
    // 🔴 **応答へは出さない**（`DocumentVersionDto` から削除済み。#1011 / [[IADR-0290]]）。
    // 保持するのは「スナップショット時点で文書が指していた本文 URI」であり、キーが文書 ID で
    // 固定である以上**現行版の本文と同じ値になる**。外へ出すと「その版の本文」と読まれる。
    // 列は残す（落とすにはマイグレーションが要り、裁定は是正を求めていない）。
    public string? MarkdownUri { get; private set; }
    public Dictionary<string, string> Attributes { get; private set; } = [];
    // FR-06, SC-09, #635: **識別子**を持つ（現行版と同じ）。
    // **過去版も改名に追随して新しい名前で表示される**——改名は表示上の変更であり、同一のタグを指し続ける
    // （[[IADR-0153]] 決定 4）。**古い名前を抱えるほうが計画に反する。**
    public List<Guid> Tags { get; private set; } = [];
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
