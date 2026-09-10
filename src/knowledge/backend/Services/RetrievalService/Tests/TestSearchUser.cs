using RetrievalService.Domain;

namespace RetrievalService.Tests;

// FR-05, NFR-09, NFR-16, 計画 ADR-0086 決定 1, [[IADR-0425]] 決定 2 (#1255):
// 検索の利用者文脈がポートの**必須引数**になったため、それを気にしない試験のための既定値を置く。
//
// 🔴 **「気にしない」を明示するための器である。** 既定値をポート側へ置くと本番のコードでも
// 渡し忘れが通ってしまい、east-west gRPC の入口で呼び出し元サービスの s2s 主体が
// 利用者に化ける（`SearchUserContext` の表）。
// **渡し忘れをコンパイルで止める性質は本番のためのものであり、試験の都合で緩めない。**
internal static class TestSearchUser
{
    /// <summary>段を持たない検索の試験が使う「誰でもよい」利用者。</summary>
    internal static readonly SearchUserContext Any =
        SearchUserContext.FromBody("test-user", new Dictionary<string, string>());

    /// <summary>方式 A の転送（REST の近傍展開）が使う、転送可能な資格情報つきの利用者。</summary>
    internal static SearchUserContext WithCredential(string? authorization, string userId = "test-user") =>
        new(userId, new Dictionary<string, string>(), IsAuthenticated: true, ForwardableCredential: authorization);

    /// <summary>east-west gRPC の入口が作る形（転送できる資格情報は無い）。</summary>
    internal static SearchUserContext FromBody(
        string userId, params (string Key, string Value)[] attributes) =>
        SearchUserContext.FromBody(userId, attributes.ToDictionary(a => a.Key, a => a.Value));
}
