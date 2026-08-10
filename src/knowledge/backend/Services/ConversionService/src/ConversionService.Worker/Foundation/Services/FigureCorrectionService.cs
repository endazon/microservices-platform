using ConversionService.Worker.Foundation.Jobs;
using ConversionService.Worker.Foundation.Ports;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using MassTransit;

namespace ConversionService.Worker.Foundation.Services;

// FR-12, UC-06, SC-07, IADR-0154: 人手補正 Phase 1（図のコード化のやり直し）。
//
// **原本からの再変換にしない**（IADR-0154 決定 3）。やり直すと LLM が再び縮退して補正が消えるためで、
// 計画が「マージを採らない」と決めた理由（05_screens:334）と同じ種類の危険である。行うのは
// **本文中の図ブロック 1 つの置換**であり、置換後に DocumentNormalized を再発行して取り込みへ戻す
// （05_screens:313「補正結果は取り込みへ再投入する」）。DocumentId は決定的なので文書は同一である。
public interface IFigureCorrectionService
{
    Task<FigureCorrectionOutcome> CorrectAsync(Guid jobId, string figureId,
        FigureCorrectionRequest request, CancellationToken ct = default);
}

// 補正の結果。エンドポイントが HTTP 応答へ写像する。
public enum FigureCorrectionStatus
{
    Applied,
    JobNotFound,
    FigureNotFound,
    // Phase 1 は縮退した図に限る（コード化済みの図は対象外）。
    NotCorrectable,
    // 同一ジョブの直列化（05_screens:319）。変換中の本文を書き換えない。
    JobBusy,
    // 本文を読めなかった／図の埋め込みが見つからなかった。**補正は保存しない。**
    BodyUnavailable,
}

public record FigureCorrectionOutcome(FigureCorrectionStatus Status, string? MarkdownUri = null,
    int CorrectedFigures = 0);

public class FigureCorrectionService(
    IConversionJobStore jobs,
    IObjectStore objectStore,
    IPublishEndpoint bus,
    ILogger<FigureCorrectionService> logger) : IFigureCorrectionService
{
    public async Task<FigureCorrectionOutcome> CorrectAsync(Guid jobId, string figureId,
        FigureCorrectionRequest request, CancellationToken ct = default)
    {
        var job = await jobs.GetAsync(jobId, ct);
        if (job is null) return new(FigureCorrectionStatus.JobNotFound);
        // 変換中の本文は動いている。書き換えると変換側の保存に上書きされる。
        if (job.Status == ConversionJobStatus.Processing || job.Status == ConversionJobStatus.Queued)
            return new(FigureCorrectionStatus.JobBusy);

        var figure = await jobs.GetFigureAsync(jobId, figureId, ct);
        if (figure is null) return new(FigureCorrectionStatus.FigureNotFound);
        // Phase 1 の対象は画像保持へ縮退した図に限る（05_screens:330）。
        if (figure.Coded || string.IsNullOrEmpty(figure.ImageUri))
            return new(FigureCorrectionStatus.NotCorrectable);

        if (string.IsNullOrEmpty(job.MarkdownUri)) return new(FigureCorrectionStatus.BodyUnavailable);
        var markdown = await objectStore.TryGetMarkdownAsync(job.MarkdownUri, ct);
        if (markdown is null) return new(FigureCorrectionStatus.BodyUnavailable);

        // IADR-0154 決定 3: 埋め込み形は FigureMarkdown が単一情報源。見つからなければ**保存しない**
        // ——補正だけ保存されて本文に出ない、という壊れたと分かりにくい状態を作らないためである。
        if (!FigureMarkdown.TryReplaceImageWithCode(markdown, figureId, figure.ImageUri!,
                request.Language, request.Code, out var replaced))
        {
            logger.LogWarning(
                "Manual correction aborted for job {JobId} figure {FigureId}: image embed not found in body",
                jobId, ForLog(figureId));
            return new(FigureCorrectionStatus.BodyUnavailable);
        }

        // 本文は同じキーへ保存し直す（DocumentId が決定的なため参照先は変わらない）。
        var markdownUri = await objectStore.SaveMarkdownAsync(
            $"{job.DocumentId:N}/document.md", replaced, ct);
        await jobs.ApplyCorrectionAsync(jobId, figureId, request.Language, request.Code, markdownUri, ct);

        // 05_screens:313「補正結果は取り込みへ再投入する」。DocumentId は決定的なので重複登録にならない。
        // **ABAC 属性とタグは原本イベントから復元して載せる。** 空で再発行すると取り込み側が機密区分を
        // 読めず、文書の可視範囲が変わってしまう（IADR-0154 決定 3）。
        var source = await jobs.GetSourceEventAsync(jobId, ct);
        var figures = await jobs.ListFiguresAsync(jobId, ct);
        await bus.Publish(new DocumentNormalized(
            DocumentId: job.DocumentId!.Value,
            SourceId: job.SourceId,
            Title: Path.GetFileNameWithoutExtension(job.OriginalPath),
            MarkdownUri: markdownUri,
            // 補正した図の画像は本文から外れる。残る資産は「まだ縮退したままの図」の画像である。
            AssetUris: [.. figures?.Where(f => !f.Coded && !f.Corrected && f.ImageUri is not null)
                .Select(f => f.ImageUri!) ?? []],
            Attributes: source?.Attributes ?? new Dictionary<string, string>(),
            Tags: source?.Tags ?? [],
            NormalizedAt: DateTimeOffset.UtcNow), ct);

        logger.LogInformation("Manual correction applied: job={JobId} figure={FigureId} lang={Language}",
            jobId, ForLog(figureId), ForLog(request.Language));

        return new(FigureCorrectionStatus.Applied, markdownUri,
            figures?.Count(f => f.Corrected) ?? 0);
    }

    // 利用者由来の値をログへ出す前に無害化する（CodeQL: Log entries created from user input）。
    // `figureId` は経路パラメータ、`language` は要求本文であり、**どちらも呼び出し元が自由に決められる**。
    // 改行を残すと**偽のログ行を注入**できてしまい、後からログを読む人・機械が別の事象と誤読する。
    // 変換失敗メッセージを 1 行へ丸めている `RawDocumentFetchedConsumer.SummarizeError` と同じ趣旨で、
    // **制御文字を落とし、長さを切る**。ログ本文だけの措置であり、保存する値は変えない。
    private static string ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        var chars = value.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        var flattened = new string(chars).Trim();
        const int max = 120;
        return flattened.Length <= max ? flattened : flattened[..max] + "…";
    }
}
