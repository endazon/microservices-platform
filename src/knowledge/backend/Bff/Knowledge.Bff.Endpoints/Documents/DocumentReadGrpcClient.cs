using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Grpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Knowledge.Contracts.Grpc.Document.V1;

namespace Knowledge.Bff.Endpoints.Documents;

// FR-05, FR-06, UC-03, SC-03, SC-05, NFR-09, NFR-16, ADR-0029, ADR-0056, ADR-0075,
// [[IADR-0009]], [[IADR-0041]], [[IADR-0045]], [[IADR-0379]], [[IADR-0402]] (#1255):
// 文書台帳の読み取り 4 口（`knowledge.document.v1.DocumentRead`）の**呼び出し側**。
//
// **並走中の正は REST である。** 本クライアントは `Services:DocumentServiceGrpc` が構成された
// ときだけ登録され（`AddDocumentReadGrpcClient`）、`DocumentBffEndpoints` は登録が在れば
// こちらを使う。戻すのは構成を外すだけでよい（コードは変えない）。
//
// 🔴 **ここで縮退を畳まない。** 返すのは事実（「無い」＝`null` / 空、「引けなかった」＝例外）だけである。
//   畳み方は**呼び出し元の call site ごとに違う**（[[IADR-0400]] 決定 5 / [[IADR-0401]] 決定 5 と同じ作法）——
//   実測すると `DocumentBffEndpoints` の 4 箇所は
//     ・一覧（`FetchListAsync`）… 非 2xx も不達も **`[]`**
//     ・詳細（`FetchAuthorizedAsync`）… 非 2xx も不達も **`null`**（→ 404 秘匿）
//     ・版履歴… 捕捉なし（**500**）。本文 null は `[]`
//     ・特定版… 非 2xx は **404**、不達は捕捉なし（**500**）
//   と**4 通りに割れている**。ここで 1 つに畳むと、どれか 1 つの枝が静かに変わる。
//   したがって **REST の try/catch が在る場所へ `RpcException` を足す**形で写す
//   （枝を新設せず、既存の枝の入口を広げる）。
//
// 🔴 **利用者の資格情報は載せない**（[[IADR-0379]] 決定 4）。載るのは BFF 自身の s2s トークンだけである。
// これが成立するのは、**この 4 口が現状も利用者の資格情報を運んでいない**からである ——
// ABAC の実施点は `BffScopeResolver` ＋ `IsManageable` であり（[[IADR-0041]] / [[IADR-0045]]）、
// 呼び出し先の読み取り group はロールで塞いでいない。**移行で判定の位置を動かしていない。**
public sealed class DocumentReadGrpcClient(Pb.DocumentRead.DocumentReadClient client)
{
    /// <summary>`Services:DocumentServiceGrpc`（h2c のアドレス。例: http://document-service:8081）。</summary>
    public const string AddressKey = "Services:DocumentServiceGrpc";

    /// <summary>宛先ごとにチャネルを分けるための DI キー（下の 🔴 を参照）。</summary>
    public const string ChannelKey = "DocumentServiceGrpc";

    /// <summary>FR-06, UC-03: 文書の一覧（更新の新しい順）。引けなければ例外（呼び出し元が畳む）。</summary>
    public async Task<List<DocumentDto>> ListAsync(CancellationToken ct)
    {
        var resp = await client.ListDocumentsAsync(new Pb.ListDocumentsRequest(), cancellationToken: ct);
        return resp.Documents.Select(DocumentReadGrpcMapping.ToDto).ToList();
    }

    /// <summary>FR-06, UC-03: 文書 1 件。**台帳に無ければ <c>null</c>**（REST の 404 と同値）。</summary>
    public async Task<DocumentDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var resp = await client.GetDocumentAsync(
            new Pb.GetDocumentRequest { Id = id.ToString("D") }, cancellationToken: ct);
        return resp.Found ? DocumentReadGrpcMapping.ToDto(resp.Document) : null;
    }

    /// <summary>
    /// FR-06, UC-03: 版履歴。**文書そのものが無ければ <c>null</c>**（REST の 404 と同値）、
    /// 版が 1 件も無いだけなら**空リスト**（REST の 200 `[]` と同値）。この 2 つは別の事実である。
    /// </summary>
    public async Task<List<DocumentVersionDto>?> ListVersionsAsync(Guid id, CancellationToken ct)
    {
        var resp = await client.ListVersionsAsync(
            new Pb.ListVersionsRequest { DocumentId = id.ToString("D") }, cancellationToken: ct);
        return resp.Found ? resp.Versions.Select(DocumentReadGrpcMapping.ToDto).ToList() : null;
    }

    /// <summary>FR-06, UC-03, SC-03: 特定版。**無ければ <c>null</c>**（REST の 404 と同値）。</summary>
    public async Task<DocumentVersionDto?> GetVersionAsync(Guid id, int version, CancellationToken ct)
    {
        var resp = await client.GetVersionAsync(
            new Pb.GetVersionRequest { DocumentId = id.ToString("D"), Version = version },
            cancellationToken: ct);
        return resp.Found ? DocumentReadGrpcMapping.ToDto(resp.Snapshot) : null;
    }
}

public static class DocumentReadGrpcClientExtensions
{
    // `Services:DocumentServiceGrpc` が構成されたときだけ gRPC 経路を登録する。
    // 未設定なら**何も登録せず**、`DocumentBffEndpoints` は REST のまま（並走中の正は REST）。
    public static IServiceCollection AddDocumentReadGrpcClient(
        this IServiceCollection services, IConfiguration config)
    {
        var address = config[DocumentReadGrpcClient.AddressKey];
        if (string.IsNullOrWhiteSpace(address))
            return services;

        services.AddPlatformServiceToken(config);
        // 🔴 チャネルは**キー付き**で登録する。BFF は `AddAuthzScopeGrpcClient` が**キー無し**で
        // 登録する認可サービス宛のチャネルを既に持ち得る（参照実装）——
        // **BFF は 2 つの宛先を同時に持ち得る最初のホストである**。キー無しを共有すると、
        // 文書のクライアントが認可サービスへ繋がる（あるいはその逆）。
        // `AddLlmGatewayGrpcClient` が同じ理由でキー付きにしているのと同型である。
        services.AddKeyedSingleton(DocumentReadGrpcClient.ChannelKey, (sp, _) =>
            GrpcClientExtensions.CreatePlatformChannel(address, sp.GetRequiredService<IServiceTokenProvider>()));
        services.AddSingleton(sp => new Pb.DocumentRead.DocumentReadClient(
            sp.GetRequiredKeyedService<GrpcChannel>(DocumentReadGrpcClient.ChannelKey)));
        services.AddSingleton<DocumentReadGrpcClient>();
        return services;
    }
}
