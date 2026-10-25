using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Knowledge.Contracts.Grpc.Ingestion.V1;

namespace Knowledge.Bff.Endpoints.Documents;

// FR-06, SC-05, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0009]], [[IADR-0379]], [[IADR-0402]], [[IADR-0533]],
// [[IADR-0565]] 決定 3・4 (#1993): 文書の索引の状態（`knowledge.ingestion.v1.DocumentIndexStatus`）の**呼び出し側**。
//
// 🔴 **渡すのは ABAC を通った窓の中の文書の ID だけである**（呼び出し元 `DocumentBffEndpoints` が窓を切り出した後に呼ぶ）。
// 権限外の文書の ID を取り込み側へ出さない（IADR-0009 の存在秘匿。取り込み側の面は利用者の文脈を持たず、渡された ID を信じる）。
//
// 🔴 **1 ページ分を 1 回で引く**（N+1 にしない）。上限は取り込み側と同じ 100（＝窓の上限 `DocumentListWindow.MaxLimit`）。
//
// 🔴 **引けなかったら全件 `unknown` へ縮退し、一覧そのものは失敗させない**（参照実装の deny-by-default と同じ向きの縮退。IADR-0379 決定 4）。
// 縮退の範囲は rpc の全 status（宛先未構成の `UNAVAILABLE`・期限切れを含む）と s2s トークン取得失敗。呼び出し元の取り消しは外へ出す。
//
// 🔴 **利用者の資格情報は載せない**（[[IADR-0379]] 決定 4）。載るのは BFF 自身の s2s トークンだけである。
public sealed class DocumentIndexStatusGrpcClient(Pb.DocumentIndexStatus.DocumentIndexStatusClient client)
{
    /// <summary>`Services:IngestionServiceGrpc`（h2c のアドレス。例: http://ingestion-service:8081）。</summary>
    public const string AddressKey = "Services:IngestionServiceGrpc";

    /// <summary>宛先ごとにチャネルを分けるための DI キー（[[IADR-0402]] 決定 6）。</summary>
    public const string ChannelKey = "IngestionServiceGrpc";

    /// <summary>
    /// 1 回の照会の期限。索引の状態は一覧の付け足しであり、これを待って一覧の表示を遅らせない（超えたら `unknown`）。
    /// </summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 窓の中の文書の索引の状態（文書 ID の文字列 → `DocumentIndexStatuses` の値）。鍵は渡した ID だけ。
    /// 空の窓は照会しない。引けなければ全件 `unknown`（WARN 1 行）。
    /// </summary>
    public async Task<Dictionary<string, string>> GetStatusesAsync(
        HttpContext http, IReadOnlyList<Guid> documentIds, CancellationToken ct)
    {
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        if (documentIds.Count == 0)
            return statuses;
        foreach (var id in documentIds)
            statuses[id.ToString()] = DocumentIndexStatuses.Unknown;

        var request = new Pb.GetIndexStatusesRequest();
        request.DocumentIds.AddRange(documentIds.Select(id => id.ToString()));
        Pb.GetIndexStatusesResponse response;
        try
        {
            response = await client.GetStatusesAsync(request, deadline: DateTime.UtcNow.Add(Deadline), cancellationToken: ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is RpcException or InvalidOperationException)
        {
            DocumentReadFailureLog.Folded(http, DocumentReadFailureLog.GetIndexStatusesRpc, ex, "unknown");
            return statuses;
        }

        // 渡していない ID は載せない（取り込み側が余計な鍵を返しても、窓の外の鍵を応答へ出さない）。
        foreach (var state in response.States)
        {
            if (Guid.TryParse(state.DocumentId, out var id) && statuses.ContainsKey(id.ToString()))
                statuses[id.ToString()] = FromProto(state.Status);
        }
        return statuses;
    }

    // 🔴 `UNSPECIFIED`・未知の値は「分からない」（`unknown`）。未索引へ倒さない。
    public static string FromProto(Pb.IndexStatus status) => status switch
    {
        Pb.IndexStatus.Indexed => DocumentIndexStatuses.Indexed,
        Pb.IndexStatus.EmbeddingPending => DocumentIndexStatuses.EmbeddingPending,
        Pb.IndexStatus.NotIndexed => DocumentIndexStatuses.NotIndexed,
        _ => DocumentIndexStatuses.Unknown,
    };
}

// FR-06, SC-05, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 4, [[IADR-0402]] 決定 6, [[IADR-0533]] 決定 2,
// [[IADR-0565]] (#1993): IngestionService 宛の生成クライアントの登録。
public static class DocumentIndexStatusGrpcClientExtensions
{
    // **常に登録する。** 構成が無ければ常に `UNAVAILABLE` を返す呼び出し器の上に組む（[[IADR-0533]] 決定 2。状態は `unknown` へ縮退する）。
    public static IServiceCollection AddDocumentIndexStatusGrpcClient(
        this IServiceCollection services, IConfiguration config)
    {
        var address = config[DocumentIndexStatusGrpcClient.AddressKey];
        if (string.IsNullOrWhiteSpace(address))
        {
            services.TryAddUnconfiguredGrpcClient(
                DocumentIndexStatusGrpcClient.AddressKey, ci => new Pb.DocumentIndexStatus.DocumentIndexStatusClient(ci));
            services.TryAddSingleton<DocumentIndexStatusGrpcClient>();
            return services;
        }

        services.AddPlatformServiceToken(config);
        // 🔴 チャネルは**キー付き**・`TryAdd`（[[IADR-0402]] 決定 6 / [[IADR-0412]] 決定 5）。BFF は複数の宛先を同時に持つ。
        services.TryAddKeyedSingleton<GrpcChannel>(DocumentIndexStatusGrpcClient.ChannelKey, (sp, _) =>
            GrpcClientExtensions.CreatePlatformChannel(address, sp.GetRequiredService<IServiceTokenProvider>()));
        services.TryAddSingleton(sp => new Pb.DocumentIndexStatus.DocumentIndexStatusClient(
            sp.GetRequiredKeyedService<GrpcChannel>(DocumentIndexStatusGrpcClient.ChannelKey)));
        services.TryAddSingleton<DocumentIndexStatusGrpcClient>();
        return services;
    }
}
