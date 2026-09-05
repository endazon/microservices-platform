namespace Platform.Shared.Infrastructure.Foundation.Grpc;

// NFR-09, IADR-0379 決定 4 (#1201): 呼び出し側サービス自身の資格情報（s2s トークン）を返す。
// 取得できないときは例外（呼び出し側は deny-by-default へ縮退させる）。
public interface IServiceTokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken ct);
}
