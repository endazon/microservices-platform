using Amazon.S3;
using ConversionService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace ConversionService.Features.ConversionJobs.GetFigureImage;

// FR-12, UC-06, SC-07, ADR-0014, ADR-0109 決定 1・3, [[IADR-0540]], [[IADR-0558]] (#1941): 縮退した図の元画像の読み取り。
//
// 🔴 **画像を読むのは図の所有者（変換サービス）である。** BFF はオブジェクトストレージの資格情報を持たず、利用者の資格情報を付けた
//   中継（エッジ）でこの口を呼ぶ。以前の BFF はストレージを直接読み、helm・compose で未配線のまま `NullObjectStorageClient` へ縮退して
//   配備では常に 404 だった（#1941。文書の本文の #1905 / [[IADR-0544]] と同じ構図）。
//
// 🔴 **画像の状態は分ける。「画像が無い」に畳まない。**
//   - 在る … バイト列と Content-Type
//   - ジョブが無い・図が無い・コード化済み（画像を持たない）… NotFound（IADR-0154「未知の図と区別しない」）
//   - 参照が storage:// でない … NotStored。外部 URL は取りに行かない（任意の宛先への取得口を作らない）
//   - オブジェクトが無い（NoSuchKey。[[IADR-0540]] 決定 1 と同じ判定）… Missing
//   ストレージが構成されていない（`CanResolve` が偽）は `FigureImageStorageUnavailableException`、一時障害（時間切れ・5xx・
//   バケットが無い）は例外のまま上げる —— 「画像が無い」と「引けなかった」を分ける。
public sealed class FigureImageReader(
    IConversionJobStore jobs,
    IObjectStorageClient storage,
    ILogger<FigureImageReader> logger)
{
    public async Task<FigureImageResult> ReadAsync(Guid jobId, string figureId, CancellationToken ct = default)
    {
        var figure = await jobs.GetFigureAsync(jobId, figureId, ct);
        if (figure?.ImageUri is null)
            return FigureImageResult.NotFound();

        var uri = figure.ImageUri;
        if (!StorageUri.IsStorageUri(uri))
            return FigureImageResult.NotStored();

        if (!storage.CanResolve(uri))
            throw new FigureImageStorageUnavailableException(
                "オブジェクトストレージが構成されていないため、図の画像を読めません（ObjectStorage:Endpoint）。");

        byte[] bytes;
        try
        {
            bytes = await storage.GetBytesAsync(uri, ct);
        }
        catch (AmazonS3Exception ex) when (ObjectStorageErrors.IsNoSuchKey(ex))
        {
            // 基数はジョブ × 図で閉じる（利用者識別子・画像は載せない）。
            logger.LogWarning(
                "Figure image object missing (NoSuchKey) for job {JobId} figure {FigureId} at {ImageUri}",
                jobId, figureId, uri);
            return FigureImageResult.Missing();
        }

        return FigureImageResult.Available(bytes, figure.ImageContentType ?? "application/octet-stream");
    }
}

public enum FigureImageStatus
{
    Available,
    NotFound,
    NotStored,
    Missing,
}

public sealed record FigureImageResult(FigureImageStatus Status, byte[]? Bytes, string? ContentType)
{
    public static FigureImageResult Available(byte[] bytes, string contentType) =>
        new(FigureImageStatus.Available, bytes, contentType);
    public static FigureImageResult NotFound() => new(FigureImageStatus.NotFound, null, null);
    public static FigureImageResult NotStored() => new(FigureImageStatus.NotStored, null, null);
    public static FigureImageResult Missing() => new(FigureImageStatus.Missing, null, null);
}

/// <summary>ストレージが構成されていない（`NullObjectStorageClient`）。構成の不調であって「画像が無い」ではない。</summary>
public sealed class FigureImageStorageUnavailableException(string message) : Exception(message);
