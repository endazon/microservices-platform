using Knowledge.Contracts.Dtos;

namespace DocumentService.Api.Foundation.Domain;

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
    // FR-18, ADR-0050 決定 1 (#911): 本文指紋（正規化 Markdown の SHA-256 小文字 hex）。
    // **本文が変われば変わり、変わらなければ変わらない**ことだけが契約（DocumentUpdated が運ぶ）。
    // 本文を持たない／指紋化できなかった文書は null（発行側の縮退。下流は「不明」として扱う）。
    public string? ContentFingerprint { get; private set; }
    // FR-06, FR-09, SC-09, #635: **タグの識別子**を持つ。**表示名を複写しない**
    // （計画確定「辺は型の識別子を参照して保持し、表示名を複写しない」。[[IADR-0153]] 決定 1）。
    // 複写すると**改名時に古い名前のまま取り残される**。表示名への解決は `DocumentEndpoints` が行う。
    public List<Guid> Tags { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    // FR-06, UC-03: 版履歴（append-only）。作成・各更新で確定版のスナップショットを追記する。
    public IReadOnlyList<DocumentVersion> Versions => _versions;

    private Document() { }

    public static Document Create(string title, string? originalUri, string? contentType,
        Dictionary<string, string>? attributes = null, List<Guid>? tags = null)
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
        Dictionary<string, string>? attributes = null, List<Guid>? tags = null,
        string? contentFingerprint = null)
    {
        var doc = new Document
        {
            Id = id,
            Title = title,
            MarkdownUri = markdownUri,
            Status = DocumentStatus.Normalized,
            Attributes = attributes ?? [],
            Tags = tags ?? [],
            ContentFingerprint = contentFingerprint,
        };
        doc.Snapshot("normalized");
        return doc;
    }

    // FR-21, UC-03: **本文を伴う登録**（文書本文の直接受け入れ経路）。
    // 本文はオブジェクトストレージへ格納済みであり、ここで受け取るのは参照 URI だけである
    // （受け入れ基準 ④「DB は参照のみ持つ」）。
    //
    // **`OriginalUri` を同時に受ける**——受け入れ基準 ③ は本文と `OriginalUri` を排他にせず
    // **併存**させることを要求している。`CreateNormalized`（取り込み経路）は `OriginalUri` を
    // 持たないため、そちらを流用すると ③ が満たせない。
    //
    // 状態は `normalized` である。本文は正規化済み Markdown としてそのまま受け入れるので、
    // 変換（ConversionService）を経た文書と同じ状態に置く。**この状態が取り込みと Wiki 同期の
    // 起動条件**であり、①（取り込み・分割・埋め込みが起動する）はここで成立する。
    public static Document CreateWithBody(Guid id, string title, string markdownUri,
        string? originalUri, string? contentType,
        Dictionary<string, string>? attributes = null, List<Guid>? tags = null,
        string? contentFingerprint = null)
    {
        var doc = new Document
        {
            Id = id,
            Title = title,
            MarkdownUri = markdownUri,
            OriginalUri = originalUri,
            ContentType = contentType,
            Status = DocumentStatus.Normalized,
            Attributes = attributes ?? [],
            Tags = tags ?? [],
            ContentFingerprint = contentFingerprint,
        };
        doc.Snapshot("created-with-body");
        return doc;
    }

    public void Update(string title, Dictionary<string, string> attributes, List<Guid> tags,
        string? changeNote = null)
    {
        Title = title;
        Attributes = attributes;
        Tags = tags;
        Touch();
        Snapshot(changeNote ?? "updated");
    }

    // FR-06, UC-03: メタデータ（属性・タグ）のみを更新する。本文・タイトルは変更しない。
    public void UpdateMetadata(Dictionary<string, string> attributes, List<Guid> tags,
        string? changeNote = null)
    {
        Attributes = attributes;
        Tags = tags;
        Touch();
        Snapshot(changeNote ?? "metadata-updated");
    }

    // FR-01, UC-04: 同一文書の DocumentNormalized 再配信時に正規化内容を反映する（冪等更新）。
    //
    // **［#637］タグ欄は上書きしない**（計画確定・2026-08-09。SC-05「再正規化はタグ欄を上書きしない」）。
    // **取り込み経路はタグを生成しない**ので（[[IADR-0153]] 決定 5）、ここで上書きすると
    // **SC-05 で管理者が付けたタグが再同期のたびに空で消える**。
    // **「取り込みはタグを作らない」と「取り込みはタグを消す」は別**であり、後者は SC-05 のタグ設定を無意味にする。
    //
    // **属性は上書きしてよい** —— ソースのメタ（所在・部門・フォルダ等）の写像先は ABAC 基本属性であり、
    // **取り込みが正本である**（タグとは出どころが違う）。
    //
    // **画面からの更新（`Update` / `UpdateMetadata`）は従来どおりタグを更新する。**
    // そちらは利用者が意図した更新であり、止めるとタグを外せなくなる。
    public void ApplyNormalized(string title, string markdownUri,
        Dictionary<string, string> attributes, string? contentFingerprint = null)
    {
        Title = title;
        MarkdownUri = markdownUri;
        Status = DocumentStatus.Normalized;
        Attributes = attributes;
        // ADR-0050 (#911): 再正規化は本文の変更であり、指紋を進める。null は「指紋化できなかった」
        // （ストレージ縮退等）で、下流は「不明」として扱う（解除判定を発火させない側に倒す）。
        ContentFingerprint = contentFingerprint;
        Touch();
        Snapshot("re-normalized");
    }

    public void SetMarkdownUri(string uri, string? contentFingerprint = null)
    {
        MarkdownUri = uri;
        Status = DocumentStatus.Normalized;
        ContentFingerprint = contentFingerprint;
        Touch();
        Snapshot("markdown-set");
    }

    // FR-19, FR-20, ADR-0050 (#911): 本文を書いた経路（Obsidian 同期の版適用等）が指紋を記録する。
    // 版・時刻は呼び出し側の Update/Snapshot が進める（ここでは動かさない）。
    public void RecordContentFingerprint(string? contentFingerprint)
        => ContentFingerprint = contentFingerprint;

    // FR-19, FR-21 受け入れ基準 ⑨, [[IADR-0283]] 決定 4:
    // 個人資料の露出トグル「AI の入力に含める」を ABAC 文書属性へ写す。
    //
    // 🔴 **版・時刻を動かさない**（`Touch()` / `Snapshot()` を呼ばない）。**露出トグルは本文の編集
    // ではない** —— FR-19 は「編集の回数だけ版を保持」と定めており、トグルで版が増えると
    // （a）版履歴が編集以外で膨らみ、（b）Obsidian 同期の `baseVersion` が動いてプラグインが
    // 409 を受ける。`RecordContentFingerprint` と同じ「版を進めない設定点」である。
    //
    // **辞書は差し替える**（その場で書き換えない）—— jsonb 変換器の値比較器はスナップショットとの
    // 比較で変更を検出するが、参照ごと差し替えるほうが意図が読める。
    public void SetAiInputExposure(bool includeInAi)
        => Attributes = new Dictionary<string, string>(Attributes)
        {
            [AiInputExposure.AttributeKey] = AiInputExposure.FromToggle(includeInAi),
        };

    // FR-06, UC-03, SC-05: 公開する。アーカイブ済み（非公開化済み）からの再公開は状態遷移の意図に反する
    // ため認めない（UI だけでなくドメイン不変条件としても強制する。レビュー #171 指摘対応）。
    public void Publish()
    {
        if (Status == DocumentStatus.Archived)
            throw new InvalidDocumentStateException(
                $"アーカイブ済みの文書は公開できません（id={Id}）。再公開する場合は再取り込みが必要です。");
        Status = DocumentStatus.Published;
        Touch();
        Snapshot("published");
    }

    // SC-05: この文書が現在の状態から公開可能か（draft / normalized / published からのみ。archived は不可）。
    public bool CanPublish => Status != DocumentStatus.Archived;

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

// FR-06, UC-03, SC-05: 不正な状態遷移（例: archived → published）を表すドメイン例外。
// エンドポイントは 409 Conflict へ写像する。
public sealed class InvalidDocumentStateException(string message) : InvalidOperationException(message);
