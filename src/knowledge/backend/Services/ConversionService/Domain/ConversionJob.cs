using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;

namespace ConversionService.Domain;

// FR-12, UC-06, SC-07, IADR-0042/IADR-0043: 変換ジョブの読み取りモデル（永続化エンティティ）。
// 変換コンシューマが受信・成功・失敗の各ライフサイクルを記録する。id は原本取得イベントの FetchId。
// 再変換（人手補正）で原本イベント RawDocumentFetched を再構成できるよう原本項目も保持する。
public class ConversionJob
{
    public Guid Id { get; private set; } // = RawDocumentFetched.FetchId
    public Guid SourceId { get; private set; }
    public string SourceType { get; private set; } = string.Empty;
    public string OriginalPath { get; private set; } = string.Empty;
    public string Status { get; private set; } = ConversionJobStatus.Queued;
    public string? Error { get; private set; }
    public Guid? DocumentId { get; private set; }
    public string? MarkdownUri { get; private set; }
    public int Attempts { get; private set; }

    // FR-12, SC-07, IADR-0137 / ADR-0053 決定 2: この失敗で**自動再試行を使い切ったか**（failed の内訳）。
    // 導出（Attempts >= 上限）にしないのは、Attempts が手動再変換をまたいで累積するためである。
    public bool DeadLettered { get; private set; }

    // FR-12, SC-07, ADR-0070 決定 3 / IADR-0356 (#1192) / [[IADR-0381]] (#1254):
    // **原本が本文を持っていたか**（succeeded の内訳）。テキスト層を持たない PDF は再試行しても
    // 結果が変わらないため、failed に置かず succeeded として確定させる。DeadLettered と同型の
    // 「状態の 5 値目ではない標識」である。**処理を再開したら `true`（本文あり）へ戻す** ——
    // 標識は最後に確定した変換の結果であり、走っている最中の値ではない。
    //
    // 🔴 **［2026-09-05 / #1254］否定形 `BodyAbsent` から改名し、極性を反転した**（[[IADR-0381]] 決定 1）。
    public bool HasBody { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    // FR-12, UC-06, SC-07, IADR-0154: 抽出した図の記録（人手補正 Phase 1 の対象）。
    // 変換が成功した時点で洗い替える（再変換は図を作り直すため）。
    public List<ConversionJobFigure> Figures { get; private set; } = [];

    // 再変換のための原本イベント項目（RawDocumentFetched の再構成に用いる。DTO には射影しない）。
    public string StorageUri { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public Dictionary<string, string> Attributes { get; private set; } = [];
    public List<string> Tags { get; private set; } = [];
    public DateTimeOffset FetchedAt { get; private set; }

    private ConversionJob() { }

    // 初回受信：processing・attempts=1 で新規作成。
    public static ConversionJob StartNew(RawDocumentFetched ev)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new ConversionJob
        {
            Id = ev.FetchId,
            Status = ConversionJobStatus.Processing,
            Attempts = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        job.ApplyEvent(ev);
        return job;
    }

    // 再受信・再試行の都度：processing へ・attempts++・エラー消去・原本を更新。
    // SC-07: 処理を再開したらデッドレター標識は落とす（「processing なのにデッドレター」を出さない）。
    public void MarkProcessing(RawDocumentFetched ev)
    {
        Status = ConversionJobStatus.Processing;
        Error = null;
        DeadLettered = false;
        HasBody = true;
        Attempts++;
        UpdatedAt = DateTimeOffset.UtcNow;
        ApplyEvent(ev);
    }

    // IADR-0154 決定 1: 図の記録は成功のたびに洗い替える（再変換は図を作り直すため、
    // 前回の図をそのまま残すと「どの図が今の本文に居るか」が割れる）。
    // ADR-0070 決定 3 / IADR-0356 / [[IADR-0381]]: hasBody＝原本が本文を持っていたか
    // （`false` はテキスト層の無い PDF を「本文なし」で完了させたことを表す）。
    // **状態は succeeded のまま**である（failed に置くと再変換の対象として並び、何度やっても結果の
    // 変わらないジョブが溜まる）。
    public void MarkSucceeded(Guid documentId, string markdownUri,
        IReadOnlyList<ConversionJobFigure>? figures = null, bool hasBody = true)
    {
        Status = ConversionJobStatus.Succeeded;
        Error = null;
        DocumentId = documentId;
        MarkdownUri = markdownUri;
        HasBody = hasBody;
        UpdatedAt = DateTimeOffset.UtcNow;
        if (figures is null) return;
        Figures.Clear();
        Figures.AddRange(figures);
    }

    // UC-06: 人手補正で本文を差し替えたときに、参照先だけを更新する（状態は succeeded のまま）。
    // **再変換ではない**——原本からやり直すと LLM が再び縮退して補正が消えるためである
    // （IADR-0154 決定 3）。
    public void MarkCorrected(string markdownUri)
    {
        MarkdownUri = markdownUri;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    // SC-07 / ADR-0053 決定 2: deadLettered は「この失敗で自動再試行を使い切った」ことを
    // コンシューマから受け取る。再試行の余地が残る失敗では立たない（failed の内訳を区別するため）。
    public void MarkFailed(string error, bool deadLettered = false)
    {
        Status = ConversionJobStatus.Failed;
        Error = error;
        DeadLettered = deadLettered;
        HasBody = true;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    // UC-06: **再変換**は失敗ジョブに限る。失敗以外（processing/succeeded/queued）は再変換不可。
    // **「人手補正」と混同しない**——人手補正 Phase 1（図のコード化のやり直し）の対象は
    // **画像保持へ縮退した図を持つ成功ジョブ**である（UC-06 は縮退を例外フローの中の正常な収束として
    // 定めている。IADR-0154 決定 5）。同じ語で 2 つの操作を指さないこと。
    // SC-07: 再変換を受け付けた時点でデッドレター標識は落とす（queued は「自動では直らない」状態ではない）。
    public bool TryRequeue()
    {
        if (Status != ConversionJobStatus.Failed) return false;
        Status = ConversionJobStatus.Queued;
        Error = null;
        DeadLettered = false;
        HasBody = true;
        UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    // 再変換用に原本イベントを再構成する（防御的コピー）。
    public RawDocumentFetched ToEvent() =>
        new(Id, SourceId, SourceType, OriginalPath, StorageUri, ContentType,
            new Dictionary<string, string>(Attributes), [.. Tags], FetchedAt);

    // SC-07: 試行上限は全ジョブ共通の設定値であり、画面がしきい値を推測しなくて済むよう毎行へ射影する。
    // IADR-0127「状態表示は契約から導出できる値だけで作る」: 図の内訳と「補正あり」も毎行へ射影する
    // （画面が図の一覧を全ジョブ分引かずに一覧を描けるようにするため）。
    public ConversionJobDto ToDto() =>
        new(Id, SourceId, SourceType, OriginalPath, Status, Error, DocumentId, MarkdownUri,
            Attempts, CreatedAt, UpdatedAt, DeadLettered, ConversionJobRetryPolicy.MaxAttempts,
            Figures.Count(f => f.Coded), Figures.Count(f => !f.Coded), Figures.Any(f => f.Corrected),
            HasBody);

    // UC-06: 再変換で失われる補正の件数（409 corrections_would_be_lost の本文へ載せる）。
    public int CorrectedFigureCount => Figures.Count(f => f.Corrected);

    private void ApplyEvent(RawDocumentFetched ev)
    {
        SourceId = ev.SourceId;
        SourceType = ev.SourceType;
        OriginalPath = ev.OriginalPath;
        StorageUri = ev.StorageUri;
        ContentType = ev.ContentType;
        Attributes = new Dictionary<string, string>(ev.Attributes);
        Tags = [.. ev.Tags];
        FetchedAt = ev.FetchedAt;
    }
}
