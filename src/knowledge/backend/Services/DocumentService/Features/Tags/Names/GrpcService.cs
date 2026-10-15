using DocumentService.Infrastructure.Persistence;
using Grpc.Core;
using Knowledge.Contracts.Grpc.Document.V1;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Tags.Names;

// FR-18, NFR-09, NFR-16, SC-09, ADR-0029, ADR-0043, ADR-0063 決定 2, ADR-0075,
// [[IADR-0299]], [[IADR-0364]] 決定 2, [[IADR-0379]], [[IADR-0401]], [[IADR-0402]],
// [[IADR-0410]], [[IADR-0412]] (#1255): タグ辞書読み取りの **east-west gRPC 面**。
//
// 🔴 **本体は持たない。** `TagNamesQuery.ReadNamesAsync` を呼ぶだけである（問い合わせを 2 つにしない）。
// ［2026-10-15 / #1902・[[IADR-0533]]］無認証の REST `GET /internal/tags/names` は撤去した。名前の面はこれだけである。
//
// 🔴 **ServiceCaller を要求する**（[[IADR-0379]] 決定 4。`platform-service`）。撤去した REST の受け口は
// 認証を持たなかった（[[IADR-0364]] 決定 2）—— 狭める判断は撤去の段（#1902）で済んだ。
//
// 🔴 **利用者の文脈を受け取らない。** 読む主体は GraphService 自身である（[[IADR-0364]] 決定 2）——
// 辞書は提案の**生成段**で LLM に選ばせる値集合であり、利用者へ返すものではない。
// 要求 message に `user_id` も `user_attributes` も無いのはそのためである
// （呼び出し元が要らないものを面へ出さない。[[IADR-0401]] 決定 2）。
//
// 🔴 **「引けなかった」を応答で表さない。** 辞書が空なら `names` が空の**正常応答**であり、
// 引けなかったのは gRPC status である。ここで例外を握り潰して空を返すと、
// 呼び出し元の fail-closed（`null` ならタグ提案を 1 件も作らない）が**静かに無効になる**。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class TagDictionaryGrpcService(DocumentDbContext db)
    : TagDictionary.TagDictionaryBase
{
    public override async Task<ListNamesResponse> ListNames(
        ListNamesRequest request, ServerCallContext context)
    {
        var response = new ListNamesResponse();
        response.Names.AddRange(
            await TagNamesQuery.ReadNamesAsync(db, context.CancellationToken));
        return response;
    }
}
