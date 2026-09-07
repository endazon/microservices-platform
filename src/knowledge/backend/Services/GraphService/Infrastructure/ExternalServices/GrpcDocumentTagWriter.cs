using System.Security.Claims;
using Platform.Shared.Infrastructure.Foundation.Authz;
using GraphService.Domain.Ports;
using Grpc.Core;
using Grpc.Net.Client;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

namespace GraphService.Infrastructure.ExternalServices;

// FR-05, FR-18, NFR-09, NFR-16, SC-03, SC-05, ADR-0029, ADR-0036 D-07, ADR-0063 決定 1〜3,
// ADR-0075, ADR-0080, 計画 ADR-0086 決定 1・3・5, [[IADR-0044]], [[IADR-0364]] 決定 1,
// [[IADR-0379]] 決定 4・5, [[IADR-0401]], [[IADR-0402]], [[IADR-0410]] (#1255):
// AI タグ提案の承認を DocumentService へ反映するアダプタの **gRPC 版**。
//
// **並走中の正は REST である。** 本実装は `Services:DocumentServiceGrpc` が構成されたときだけ
// 登録され（`AddDocumentTagWriteGrpcClient`）、無ければ `HttpDocumentTagWriter` のままである。
// 戻すのは構成を外すだけでよい（コードは変えない）。
//
// 🔴 **権限伝播は「利用者文脈を本文で運ぶ」へ変わった**（計画 `ADR-0086` 決定 1）。
// 従前は承認者本人の `Authorization` ヘッダを転送していた（方式 A）。メタデータに載るのは
// **本サービス自身の s2s トークン**だけであり、利用者のトークンは面を通らない
// （通すと呼び出し先が「利用者が直接呼んだ」と区別できず confused deputy が成立する）。
//
// 🔴 **後段が再判定することは変わらない**（[[IADR-0044]] の最終防衛線 / `ADR-0063` 決定 3 の
// 「①所有者 または ②管理者ロール」の選言）。**判定の位置は動いていない** —— 変わるのは
// 文脈の運び方だけである。**依存が 1 段増える**（承認者の身元が「検証済みトークン」から
// 「本サービスの主張」になる）ことは `ADR-0086` §結果 が受け入れたトレードオフである。
//
// 🔴 **realm ロールは `user_attributes` へ混ぜず `user_roles` で運ぶ**（proto の 🔴 を参照）。
//
// 🔴 **fail-closed である。縮退の枝を 1 つも増やさず・1 つも減らさない。**
//
// | 事象 | REST | ここ |
// | --- | --- | --- |
// | 付いた・既に付いていた | 2xx → `Applied` | `APPLIED` → `Applied` |
// | 辞書に無い | 400 → `UnknownTag` | `UNKNOWN_TAG` → `UnknownTag` |
// | 所有者でも管理者でもない・文書が無い | 404 → `NotWritable` | `NOT_WRITABLE` → `NotWritable` |
// | それ以外の非 2xx | LogError → `Unavailable` | `RpcException` → LogError → `Unavailable` |
// | 到達できない・トークン取得失敗 | LogError → `Unavailable` | 同上 |
//
// 🔴 **成功へ縮退しない** —— 承認できていないのに承認済みと見えるのが最悪である。
public sealed class GrpcDocumentTagWriter(
    Pb.DocumentTagWrite.DocumentTagWriteClient client,
    IHttpContextAccessor httpContextAccessor,
    ILogger<GrpcDocumentTagWriter> logger) : IDocumentTagWriter
{
    public async Task<TagWriteOutcome> AddTagAsync(
        Guid documentId, string tagName, CancellationToken ct = default)
    {
        var http = httpContextAccessor.HttpContext;

        // 🔴 **承認者が分からなければ呼ばない。値は REST と同じ `NotWritable` である。**
        // REST 版は資格情報が無いまま呼び、後段が匿名として 404 を返すので `NotWritable` になっていた
        // （`HttpDocumentTagWriterTests.Does_not_invent_credentials_when_the_request_has_none`）。
        // gRPC で空の `user_id` を送ると後段は `INVALID_ARGUMENT` を返し、それは `Unavailable` へ
        // 落ちる —— **値が変わってしまう**。だから手前で同じ値へ倒す。
        // **要求の外（バックグラウンド）から呼ばれる経路は無い**ので、この枝は多層防御である。
        if (http?.User.Identity?.IsAuthenticated != true)
        {
            logger.LogWarning(
                "タグ提案の反映を行わない（要求に承認者の資格情報が無い）。documentId={DocumentId}。",
                documentId);
            return TagWriteOutcome.NotWritable;
        }

        var request = new Pb.AddTagRequest
        {
            DocumentId = documentId.ToString(),
            TagName = tagName,
            UserId = http.User.Identity.Name ?? string.Empty,
            // 🔴 **書き込みである。既定へ丸めない**（[[IADR-0272]] 決定 4 と同じ理由）。
            Action = GraphAccessAction.Write,
        };
        foreach (var (key, value) in ExtractUserAttributes(http))
            request.UserAttributes[key] = value;
        request.UserRoles.AddRange(http.User.FindAll(ClaimTypes.Role).Select(c => c.Value));

        try
        {
            var response = await client.AddTagAsync(request, cancellationToken: ct);
            return response.Result switch
            {
                Pb.TagWriteResult.Applied => TagWriteOutcome.Applied,
                Pb.TagWriteResult.UnknownTag => TagWriteOutcome.UnknownTag,
                Pb.TagWriteResult.NotWritable => TagWriteOutcome.NotWritable,
                // 🔴 **`UNSPECIFIED` を成功に化けさせない。** proto3 の既定は 0 であり、
                // 受け口が代入を落とすと 0 が届く。**未知は `Unavailable`** である。
                _ => Unavailable(documentId, response.Result),
            };
        }
        catch (RpcException ex)
        {
            // REST の「非 2xx」と「到達できない」は gRPC では同じ `RpcException` に畳まれる。
            // どちらも `Unavailable`（呼び出し側が 502 にする）。
            logger.LogError(ex,
                "タグ提案の反映に失敗した（status={Status}）。documentId={DocumentId}。タグ値は本文へ出さない。",
                ex.StatusCode, documentId);
            return TagWriteOutcome.Unavailable;
        }
        // s2s トークンの取得失敗（`InvalidOperationException`）もここへ落ちる。
        // **呼び出し元のキャンセルだけは伝播させる**（REST 側と同じ姿勢）。
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            logger.LogError(ex, "タグ提案の反映先へ到達できない。documentId={DocumentId}。", documentId);
            return TagWriteOutcome.Unavailable;
        }
    }

    private TagWriteOutcome Unavailable(Guid documentId, Pb.TagWriteResult result)
    {
        logger.LogError(
            "タグ提案の反映の応答が未知の結果である（result={Result}）。documentId={DocumentId}。",
            result, documentId);
        return TagWriteOutcome.Unavailable;
    }

    // FR-05, ADR-0080, IADR-0411 (#1323): 抽出はプラットフォーム唯一の点へ委譲する。
    // 🔴 **ここで読むキーを列挙しない。** 同じ列挙が 6 か所に散っていたことが #1323 の欠陥であり、
    // 1 か所でも取り残すとその経路だけ判定が変わる。集合値（`tags` / `projects`）の符号化も
    // 共有点が持つ（`UserAttributeEncoding`）。
    // **この口では現に判定に使われない**（後段は所有者束縛とロールの選言である）が、
    // 利用者文脈の 3 項目を欠かさずに運ぶ（計画 `ADR-0086` 決定 1 の形）。
    private static Dictionary<string, string> ExtractUserAttributes(HttpContext ctx)
        => BffScopeResolver.ExtractUserAttributes(ctx);
}

// FR-18, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 4・5, [[IADR-0410]] (#1255):
// DocumentService 宛の生成クライアントの登録。参照実装 `AddAuthzScopeGrpcClient` と同型。
public static class DocumentTagWriteGrpcClientExtensions
{
    /// <summary>`Services:DocumentServiceGrpc`（h2c のアドレス。例: http://document-service:8081）。</summary>
    public const string AddressKey = "Services:DocumentServiceGrpc";

    /// <summary>宛先ごとにチャネルを分けるための DI キー（下の 🔴 を参照）。</summary>
    public const string ChannelKey = "DocumentServiceGrpc";

    // 構成が無ければ**何も登録しない** —— 呼び出し元は登録の有無で REST 実装と gRPC 実装を選ぶ。
    public static IServiceCollection AddDocumentTagWriteGrpcClient(
        this IServiceCollection services, IConfiguration config)
    {
        var address = config[AddressKey];
        if (string.IsNullOrWhiteSpace(address))
            return services;

        services.AddPlatformServiceToken(config);
        // 🔴 チャネルは**キー付き**で登録する。本サービスは既に 3 つの宛先を持ち得る ——
        // 認可サービス宛（`AddAuthzScopeGrpcClient`。**キー無し**）・LlmGateway 宛・
        // ダッシュボード宛（いずれもキー付き）。4 つ目をキー無しで足すと、
        // タグの反映が**認可サービスへ繋がる**（あるいはその逆）。
        services.AddKeyedSingleton(ChannelKey, (sp, _) =>
            GrpcClientExtensions.CreatePlatformChannel(address, sp.GetRequiredService<IServiceTokenProvider>()));
        services.AddSingleton(sp => new Pb.DocumentTagWrite.DocumentTagWriteClient(
            sp.GetRequiredKeyedService<GrpcChannel>(ChannelKey)));
        return services;
    }
}
