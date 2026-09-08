using GraphService.Domain.Ports;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

namespace GraphService.Infrastructure.ExternalServices;

// FR-18, NFR-09, NFR-16, SC-09, ADR-0029, ADR-0043, ADR-0063 決定 2, ADR-0075,
// [[IADR-0364]] 決定 2, [[IADR-0379]] 決定 4・5, [[IADR-0401]], [[IADR-0402]] 決定 6,
// [[IADR-0410]], [[IADR-0412]] (#1255): タグ辞書の名前集合を **east-west gRPC** で読むアダプタ。
//
// 🔴 **REST 版（`HttpTagDictionaryReader`）と同じ意味論を保つ。** 変わるのは輸送だけである。
//
// 🔴 **利用者の資格情報を運ばない。** 読む主体は本サービス自身であり、
// メタデータに載るのは**チャネルに付いた s2s トークン**だけである（[[IADR-0379]] 決定 4）。
// 呼び出しごとのヘッダは 1 本も足さない —— REST 版が `Authorization` を付けないのと同じ理由で、
// 利用者の資格で引くと `/tags`（管理者・運用者限定）と同じ 403 になり提案が 0 件になる。
//
// 🔴 **fail-closed である。`null` は「引けなかった」、空集合は「辞書が空」。**
// `RpcException`（`UNAVAILABLE` / `PERMISSION_DENIED` / `UNAUTHENTICATED` ほか）も
// s2s トークンの取得失敗も**すべて `null`** へ倒す。**空集合へ縮退しない** ——
// 空集合は「辞書が空」という別の事実であり、混ぜると
// 「辞書が空だからタグを付けない」と「引けなかったからタグを付けない」が区別できなくなる。
// 逆に**正常応答の空リストは空集合**であって `null` ではない（**新しい枝を作らない**）。
public sealed class GrpcTagDictionaryReader(
    Pb.TagDictionary.TagDictionaryClient client,
    ILogger<GrpcTagDictionaryReader> logger) : ITagDictionaryReader
{
    public async Task<IReadOnlySet<string>?> ReadNamesAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await client.ListNamesAsync(new Pb.ListNamesRequest(), cancellationToken: ct);

            // 空白のみの要素を落とし Ordinal で集合化するのは REST 版と同じ
            // （辞書側の一意性は Trim 後の名前で保たれ、比較は DocumentService の
            // `TagResolver.ToIdsAsync` と揃える）。
            return response.Names
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (RpcException ex)
        {
            logger.LogWarning(
                "タグ辞書を gRPC で引けなかった（status={Status}）。タグ提案は生成しない。", ex.StatusCode);
            return null;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // s2s トークンの取得失敗はここへ来る（`RpcException` にならない）。
            logger.LogWarning(ex, "タグ辞書の読み取りが失敗した。タグ提案は生成しない。");
            return null;
        }
    }
}

// FR-18, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 4・5, [[IADR-0402]] 決定 6,
// [[IADR-0412]] (#1255): DocumentService 宛のタグ辞書クライアントの登録。
public static class TagDictionaryGrpcClientExtensions
{
    /// <summary>
    /// 宛先は書き込み側と**同じ** DocumentService である。したがって構成キーも
    /// `Services:DocumentServiceGrpc` を共有する —— **1 つの宛先を 2 つの鍵で切り替えない**
    /// （片方だけ gRPC へ倒れる状態が作れてしまう）。
    /// </summary>
    public const string AddressKey = DocumentTagWriteGrpcClientExtensions.AddressKey;

    /// <summary>
    /// 🔴 **チャネルは宛先ごとに 1 本**（[[IADR-0402]] 決定 6）。書き込み側と**同じキー**を使い、
    /// 同じ宛先へ 2 本目のチャネルを張らない。
    /// </summary>
    public const string ChannelKey = DocumentTagWriteGrpcClientExtensions.ChannelKey;

    // 構成が無ければ**何も登録しない** —— 呼び出し元は登録の有無で REST 実装と gRPC 実装を選ぶ。
    public static IServiceCollection AddTagDictionaryGrpcClient(
        this IServiceCollection services, IConfiguration config)
    {
        var address = config[AddressKey];
        if (string.IsNullOrWhiteSpace(address))
            return services;

        services.AddPlatformServiceToken(config);
        // 🔴 **`TryAdd` である。** 書き込み側の登録と**どちらが先でも 1 本**になる ——
        // `Add` にすると登録順で 2 本張られ、宛先ごと 1 本という決定が黙って破れる。
        services.TryAddKeyedSingleton<GrpcChannel>(ChannelKey, (sp, _) =>
            GrpcClientExtensions.CreatePlatformChannel(address, sp.GetRequiredService<IServiceTokenProvider>()));
        services.AddSingleton(sp => new Pb.TagDictionary.TagDictionaryClient(
            sp.GetRequiredKeyedService<GrpcChannel>(ChannelKey)));
        return services;
    }
}
