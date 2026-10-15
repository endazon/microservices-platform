using System.Text;
using Amazon.S3;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Features.Documents.GetContent;

// FR-06, UC-03, SC-03, ADR-0014, ADR-0029, [[IADR-0540]], [[IADR-0544]] (#1905): 現行版の本文（正規化 Markdown）の読み取り。
//
// 🔴 **本文を読むのは台帳の所有者（文書サービス）である。** BFF はオブジェクトストレージの資格情報を持たず、
//   east-west gRPC（`DocumentRead.GetDocumentContent`）で本文を受け取る（ADR-0014「ABAC 判定を通したサービス経由」）。
//   以前の BFF はストレージを直接読み、helm・compose で未配線のまま `NullObjectStorageClient` へ縮退して
//   案内文（`# 題名\n\nコンテンツは … から取得します。`）を 200 で返していた（#1905）。
//
// 🔴 **読めるかの判定は `DocumentReadUseCase.GetAsync` と同じ関数を通る**（判定器を 2 つにしない）。
//   読めない・無いは `null`（呼び出し元が found=false へ写す）。
//
// 🔴 **本文の状態は 4 つに分ける。どれも案内文の本文に畳まない**（#1905 の本質）。
//   - 在る … 本文を返す
//   - 参照が storage:// でない（未設定を含む）… NotStored。外部 URL は取りに行かない（任意の宛先への取得口を作らない）
//   - オブジェクトが無い（NoSuchKey。[[IADR-0540]] 決定 1 と同じ判定）… Missing
//   - 上限を超える … TooLarge（切り詰めない。本文の受け入れの 413 と同じ向き）
//   ストレージが構成されていない（`CanResolve` が偽）は `ObjectStorageUnavailableException`、一時障害（時間切れ・5xx・
//   バケットが無い）は例外のまま上げる —— 「本文が無い」と「引けなかった」を分ける。
public sealed class DocumentContentUseCase(
    DocumentReadUseCase reads,
    IObjectStorageClient storage,
    ILogger<DocumentContentUseCase> logger)
{
    /// <summary>
    /// 本文の上限（UTF-8 のバイト数）。16 MiB。BFF の D-1 チャネルの受信上限（既定 64 MiB）より十分に小さく、
    /// 本文の直接投入の上限（1 MB）より十分に大きい。根拠は [[IADR-0544]] 決定 4。
    /// </summary>
    public const long MaxBodyBytes = 16L * 1024 * 1024;

    public async Task<DocumentContentResult?> ReadAsync(
        DocumentReadPrincipal principal, Guid id, CancellationToken ct = default)
    {
        var doc = await reads.GetAsync(principal, id, ct);
        if (doc is null)
            return null;

        var uri = doc.MarkdownUri;
        if (!StorageUri.IsStorageUri(uri))
            return DocumentContentResult.NotStored();

        if (!storage.CanResolve(uri))
            throw new ObjectStorageUnavailableException(
                "オブジェクトストレージが構成されていないため、本文を読めません（ObjectStorage:Endpoint）。");

        string markdown;
        try
        {
            markdown = await storage.GetTextAsync(uri!, ct);
        }
        catch (AmazonS3Exception ex) when (ObjectStorageErrors.IsNoSuchKey(ex))
        {
            // 基数は文書数で閉じる（利用者識別子・本文は載せない）。
            logger.LogWarning(
                "Document body object missing (NoSuchKey) for document {DocumentId} at {MarkdownUri}", id, uri);
            return DocumentContentResult.Missing();
        }

        var size = Encoding.UTF8.GetByteCount(markdown);
        if (size > MaxBodyBytes)
        {
            logger.LogWarning(
                "Document body of {DocumentId} is {SizeBytes} bytes, over the limit {LimitBytes}; not returned",
                id, size, MaxBodyBytes);
            return DocumentContentResult.TooLarge(size, MaxBodyBytes);
        }

        return DocumentContentResult.Available(markdown);
    }
}

public enum DocumentContentStatus
{
    Available,
    NotStored,
    Missing,
    TooLarge,
}

public sealed record DocumentContentResult(
    DocumentContentStatus Status, string? Markdown, long SizeBytes, long LimitBytes)
{
    public static DocumentContentResult Available(string markdown) => new(DocumentContentStatus.Available, markdown, 0, 0);
    public static DocumentContentResult NotStored() => new(DocumentContentStatus.NotStored, null, 0, 0);
    public static DocumentContentResult Missing() => new(DocumentContentStatus.Missing, null, 0, 0);
    public static DocumentContentResult TooLarge(long size, long limit) => new(DocumentContentStatus.TooLarge, null, size, limit);
}

/// <summary>ストレージが構成されていない（`NullObjectStorageClient`）。構成の不調であって「本文が無い」ではない。</summary>
public sealed class ObjectStorageUnavailableException(string message) : Exception(message);
