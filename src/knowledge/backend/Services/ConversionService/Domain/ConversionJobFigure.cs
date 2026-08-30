using Knowledge.Contracts.Dtos;

namespace ConversionService.Domain;

// FR-12, UC-06, SC-07, IADR-0154: 変換ジョブが抽出した図の記録（人手補正 Phase 1 の対象）。
//
// **なぜ本文 Markdown からの再抽出にしないか**（IADR-0154 決定 1）:
// 本文はオブジェクトストレージにあり、一覧のたびに全ジョブ分を読むことになる。加えて、コード化に
// 成功した図（```mermaid ブロック）と、利用者が原本に書いた Mermaid ブロックは本文の形だけからは
// 見分けが付かない。補正の有無に至っては本文からは分からない。よってジョブの子として持つ。
public class ConversionJobFigure
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid JobId { get; private set; }

    // 原本から抽出したときの図 ID（ジョブ内で一意）。本文の埋め込み位置を突き止める鍵でもある。
    public string FigureId { get; private set; } = string.Empty;

    // LLM によるコード化に成功したか。false は画像保持へ縮退した図（＝人手補正の対象）である。
    public bool Coded { get; private set; }

    public string? Language { get; private set; }
    public string? Code { get; private set; }

    // 縮退した図の画像。コード化に成功した図は画像を保持しないため null。
    public string? ImageUri { get; private set; }
    public string? ImageContentType { get; private set; }
    public string? Caption { get; private set; }

    // 人手補正で Code が入ったか。**自動コード化と区別する**——「補正あり」の標識と、
    // 再変換で失われる件数がここから導出される。
    public bool Corrected { get; private set; }
    public DateTimeOffset? CorrectedAt { get; private set; }

    private ConversionJobFigure() { }

    public static ConversionJobFigure Record(Guid jobId, string figureId, bool coded,
        string? language, string? code, string? imageUri, string? imageContentType, string? caption)
        => new()
        {
            JobId = jobId,
            FigureId = figureId,
            Coded = coded,
            Language = language,
            Code = code,
            ImageUri = imageUri,
            ImageContentType = imageContentType,
            Caption = caption,
        };

    // UC-06: 人手補正を適用する。**Phase 1 は縮退した図に限る**ため、コード化済みの図は受け付けない
    // （05_screens:330）。呼び出し側は false を 409 figure_not_correctable へ写像する。
    public bool TryApplyCorrection(string language, string code)
    {
        if (Coded) return false;
        Language = language;
        Code = code;
        Corrected = true;
        CorrectedAt = DateTimeOffset.UtcNow;
        return true;
    }

    // UC-06: 再変換は補正を破棄する（マージは採らない。05_screens:334）。
    // 明示確認つきの retry からのみ呼ばれる。
    public void DiscardCorrection()
    {
        if (!Corrected) return;
        Language = null;
        Code = null;
        Corrected = false;
        CorrectedAt = null;
    }

    public ConversionFigureDto ToDto() =>
        new(FigureId, Coded, Language, Code, ImageUri, ImageContentType, Caption, Corrected, CorrectedAt);
}
