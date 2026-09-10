using Platform.Shared.Infrastructure.Foundation.Authz;

namespace RetrievalService.Domain;

// FR-03, FR-04, FR-05, FR-17, NFR-09, NFR-16, UC-01, UC-10, SC-01, SC-08, ADR-0004, ADR-0029,
// ADR-0034 決定 1, ADR-0035 決定 2, ADR-0075, 計画 ADR-0086 決定 1, ADR-0087 決定 2,
// [[IADR-0044]], [[IADR-0242]], [[IADR-0379]], [[IADR-0410]], [[IADR-0411]], [[IADR-0416]],
// [[IADR-0426]] (#1255):
// **1 回の検索が「誰のものか」を、入口から段まで運ぶ唯一の器。**
//
// 🔴 **なぜ引数で運ぶのか。** 従前、二段検索の近傍展開は利用者を
// `IHttpContextAccessor` から拾っていた。REST の入口ではそれで正しかったが、
// **east-west gRPC の入口（`DocumentSearchGrpcService`）では周辺の器に居るのは
// 呼び出し元サービスの s2s 主体である。**
//
// | 実装 | 器から拾うもの | gRPC 入口だと |
// | --- | --- | --- |
// | `GraphServiceNeighborExpander` | `Authorization` ヘッダ | **s2s トークンを GraphService へ転送する**（confused deputy） |
// | `GrpcGraphNeighborExpander` | `HttpContext.User` | **`service-account-…` が ABAC の主体に化ける** |
//
// **どちらも例外にならない。** 観測できるのは「グラフ展開が常に空」という静かな故障だけである。
// だから**器から拾わせない** —— 入口が決めた文脈を、既定値の無い必須引数で段まで運ぶ
// （渡し忘れはコンパイルで止まる）。
//
// 🔴 **属性の抽出点は `BffScopeResolver.ExtractUserAttributes` 1 つである**（[[IADR-0411]]）。
// ここでキーを列挙しない —— 同じ列挙が 6 か所に散っていたことが #1323 の欠陥である。
public sealed record SearchUserContext(
    string UserId,
    IReadOnlyDictionary<string, string> Attributes,
    bool IsAuthenticated,
    string? ForwardableCredential)
{
    /// <summary>未認証のときに応答へ載せる利用者 ID（判定には使わない）。</summary>
    public const string AnonymousUserId = "anonymous";

    /// <summary>
    /// FR-05, [[IADR-0272]] 決定 4: 検索が解決するアクション。
    /// 検索・属性値照会は閲覧経路しか持たないので read である。**既定へ頼らず明示する。**
    /// </summary>
    public const string ReadAction = "read";

    /// <summary>
    /// **north-south の入口（REST `POST /search`）** から作る。
    /// 🔴 `ForwardableCredential` は**利用者自身の資格情報**である —— 方式 A の転送
    /// （`GraphServiceNeighborExpander`）はこれだけを運ぶ。
    /// </summary>
    public static SearchUserContext FromRequest(HttpContext ctx)
    {
        var authenticated = ctx.User.Identity?.IsAuthenticated == true;
        var authorization = ctx.Request.Headers.Authorization.ToString();

        return new SearchUserContext(
            authenticated ? ctx.User.Identity!.Name ?? AnonymousUserId : AnonymousUserId,
            BffScopeResolver.ExtractUserAttributes(ctx),
            authenticated,
            string.IsNullOrEmpty(authorization) ? null : authorization);
    }

    /// <summary>
    /// **east-west の入口（gRPC `DocumentSearch/Search`）** から作る。
    /// 🔴 **`ForwardableCredential` は常に null である。** 利用者の JWT はこの面を通らない
    /// （計画 `ADR-0086` 決定 1 / [[IADR-0379]] 決定 4）—— 運ばれるのは利用者**文脈**だけであり、
    /// 呼び出し元の s2s トークンを下流へ流用してはならない。
    /// </summary>
    public static SearchUserContext FromBody(
        string userId, IReadOnlyDictionary<string, string> attributes) =>
        new(userId, attributes, IsAuthenticated: true, ForwardableCredential: null);
}
