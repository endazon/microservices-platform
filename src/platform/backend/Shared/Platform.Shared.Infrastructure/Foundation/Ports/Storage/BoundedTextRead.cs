namespace Platform.Shared.Infrastructure.Foundation.Ports.Storage;

// FR-06, NFR-16, [[IADR-0544]] 決定 4（2026-10-17 追記 / #1940）: 上限つきの本文の取得（`IObjectStorageClient.GetTextBoundedAsync`）の結果。
//
// - 上限以内: `Exceeded` が偽で、`Text` に本文、`SizeBytes` に格納されたバイト数が入る。
// - 上限超過: `Exceeded` が真で、`Text` は null（切り詰めた本文は持たない）。`SizeBytes` は応答の大きさ（ContentLength）。
//   大きさが分からず打ち切ったときは**下限**（上限＋1）である。
public sealed record BoundedTextRead(string? Text, long SizeBytes, bool Exceeded)
{
    public static BoundedTextRead Within(string text, long sizeBytes) => new(text, sizeBytes, false);

    public static BoundedTextRead Over(long sizeBytes) => new(null, sizeBytes, true);
}
