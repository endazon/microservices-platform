using System.Security.Claims;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace Knowledge.Bff.Endpoints.Search;

// FR-04, FR-05, NFR-09, NFR-16, SC-01, SC-08, ADR-0004, ADR-0029, ADR-0043, ADR-0075,
// 計画 ADR-0086 決定 1, [[IADR-0151]], [[IADR-0379]], [[IADR-0401]], [[IADR-0402]],
// [[IADR-0410]], [[IADR-0411]], [[IADR-0416]], [[IADR-0417]] (#1255):
// 権限内属性値の照会（`knowledge.retrieval.v1.AttributeValues`）の**呼び出し側**。
//
// **並走中の正は REST である。** 本クライアントは `Services:RetrievalServiceGrpc` が構成された
// ときだけ登録され（`AddAttributeValuesGrpcClient`）、`SearchBffEndpoints` は登録が在れば
// こちらを使う。戻すのは構成を外すだけでよい（コードは変えない）。
//
// 🔴 **運ぶのは利用者文脈だけであり、解決済みのスコープは運ばない**（[[IADR-0410]] / [[IADR-0416]]）。
// 呼び出し先は受け取った文脈で**自分で** `AuthzScope/Resolve` を呼ぶ。REST 面が本文の `Scope` を
// 運んでいたのは #1339 以前の名残であり、#1342 で**主張は絞り込みへ降格**している ——
// 呼び出し先が同じ認可サービスへ同じ利用者で問い合わせる以上、**送っても答えは変わらない。**
//
// 🔴 **絞り込み（`narrow_to`）は送らない。** BFF が持っている「解決済みスコープ」を
// `narrow_to` へ写すと、**分岐（`Branches`）を平たいキー単位の集合へ潰す**ことになる ——
// [[IADR-0253]] 決定 2 の非包含（キー単位 union は分岐の和の上位集合ではない）により、
// 分岐単独で到達できる文書の値が候補から落ちる。**利用者が指定した絞り込みは、この口には無い**
// （REST 面も持っていない）ので、送るものが元から無い。
//
// 🔴 **利用者の資格情報は載せない**（[[IADR-0379]] 決定 4）。載るのは BFF 自身の s2s トークンだけである。
// 利用者の同一性は**本文の `user`** が運ぶ（`ADR-0086` 決定 1）。
public sealed class AttributeValuesGrpcClient(Pb.AttributeValues.AttributeValuesClient client)
{
    /// <summary>`Services:RetrievalServiceGrpc`（h2c のアドレス。例: http://retrieval-service:8081）。</summary>
    public const string AddressKey = "Services:RetrievalServiceGrpc";

    /// <summary>宛先ごとにチャネルを分けるための DI キー（[[IADR-0402]] 決定 6）。</summary>
    public const string ChannelKey = "RetrievalServiceGrpc";

    /// <summary>
    /// FR-05, [[IADR-0272]] 決定 4: 照会が求めるアクション。属性値の照会は閲覧経路しか持たないので
    /// read である。**既定へ頼らず明示する**（受け口は空文字を read へ丸めない）。
    /// </summary>
    private const string ScopeAction = "read";

    /// <summary>
    /// FR-04, SC-01, SC-08: 到達できる文書に実際に付与された値。
    /// **引けなければ例外**（`RpcException`）であり、呼び出し元が REST と同じ枝で畳む ——
    /// ここで空配列へ縮退すると「候補が無い」と「引けなかった」が見分けられなくなる。
    /// </summary>
    public async Task<List<string>> ListValuesAsync(string key, ClaimsPrincipal user, CancellationToken ct)
    {
        var request = new Pb.ListValuesRequest { Key = key, User = ToUserContext(user) };
        var resp = await client.ListValuesAsync(request, cancellationToken: ct);
        return [.. resp.Values];
    }

    // 🔴 **載せるのは判定の入力であって判定結果ではない**（計画 `ADR-0086` 決定 1）。
    // FR-05, ADR-0080, [[IADR-0411]] (#1323): 属性の抽出はプラットフォーム唯一の点へ委譲する。
    // **ここでキーを列挙しない** —— REST 経路で BFF が `/authz/scope` へ送る属性と**同じ集合**になる。
    internal static Pb.UserContext ToUserContext(ClaimsPrincipal user)
    {
        var context = new Pb.UserContext
        {
            UserId = user.Identity?.Name ?? string.Empty,
            Action = ScopeAction,
        };
        foreach (var (key, value) in BffScopeResolver.ExtractUserAttributes(user))
            context.UserAttributes[key] = value;
        return context;
    }
}

// FR-04, FR-05, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 4・5, [[IADR-0402]] 決定 6,
// [[IADR-0417]] (#1255): RetrievalService 宛の生成クライアントの登録。
public static class AttributeValuesGrpcClientExtensions
{
    // 構成が無ければ**何も登録しない** —— 呼び出し元は登録の有無で REST 経路と gRPC 経路を選ぶ。
    public static IServiceCollection AddAttributeValuesGrpcClient(
        this IServiceCollection services, IConfiguration config)
    {
        var address = config[AttributeValuesGrpcClient.AddressKey];
        if (string.IsNullOrWhiteSpace(address))
            return services;

        services.AddPlatformServiceToken(config);
        // 🔴 チャネルは**キー付き**で登録する。BFF は既に認可サービス宛（キー無し）と
        // 文書サービス宛（`DocumentServiceGrpc`）のチャネルを持ち得る ——
        // **BFF は複数の宛先を同時に持つホストである**（[[IADR-0402]] 決定 6）。
        // キー無しを共有すると、属性値のクライアントが別のサービスへ繋がる（あるいはその逆）。
        //
        // 🔴 **`TryAdd` を使う**（[[IADR-0412]] 決定 5）—— 同じ宛先へ 2 本目のチャネルを張らせない。
        // `GetRequiredKeyedService` は最後の登録を返すので、二重登録は障害としては現れず
        // 規約だけが静かに破れる。
        services.TryAddKeyedSingleton<GrpcChannel>(AttributeValuesGrpcClient.ChannelKey, (sp, _) =>
            GrpcClientExtensions.CreatePlatformChannel(address, sp.GetRequiredService<IServiceTokenProvider>()));
        services.AddSingleton(sp => new Pb.AttributeValues.AttributeValuesClient(
            sp.GetRequiredKeyedService<GrpcChannel>(AttributeValuesGrpcClient.ChannelKey)));
        services.AddSingleton<AttributeValuesGrpcClient>();
        return services;
    }
}
