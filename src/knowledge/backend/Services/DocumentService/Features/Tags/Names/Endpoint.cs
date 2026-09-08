using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Tags.Names;

// FR-18, SC-09, ADR-0063 決定 2, IADR-0364 決定 2 (#1014): **辞書の名前だけ**を返す内部 API。
//
// GraphService が AI 提案の**生成段**で「LLM に選ばせる値集合」として引く（辺の型辞書と同じ形）。
// 生成は利用者スコープで走るが、**タグ辞書の照会口（`/tags`）は管理者・運用者限定**（SC-05 Q18）で
// あり、利用者の資格で引くと一般利用者の生成が全件 403 で 0 件になる。**読み取り主体は
// GraphService 自身**であり、辞書はプロンプトへ入るだけで利用者へ丸ごとは返らない
// （ADR-0043 決定 1 に触れない —— 利用者が見るのは LLM が選んだ提案だけである）。
//
// ★ `/internal/knowledge-health/observations`（DashboardService。[[IADR-0299]] 決定 4）と同じく
// **認証を外したメッシュ内部 API**である。第一防御は mesh の STRICT mTLS、多層防御として
// ネットワーク分離（Service は ClusterIP・NetworkPolicy 既定拒否）。OpenAPI にも載せない。
// 残余リスク（同一ネットワーク内から辞書の**名前**を読める）は IADR-0364 に受容として記録した。
//
// 🔴 **使用件数を返さない。** 件数は管理面の集計値であり、生成には要らない。
public static class TagNamesEndpoint
{
    // 🔴 読み手 GraphService.Infrastructure.ExternalServices.HttpTagDictionaryReader.NamesPath と同値。
    // **サービスを跨ぐため定数を共有できない**（サービス間は直接参照しない）。両側のテストで固定する。
    public const string NamesPath = "/internal/tags/names";

    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(NamesPath, async (DocumentDbContext db, CancellationToken ct) =>
            Results.Ok(new TagNamesResponse(await ReadNamesAsync(db, ct))))
            .WithName("InternalTagNames")
            .ExcludeFromDescription();
    }

    // FR-18, ADR-0063 決定 2, [[IADR-0412]] (#1255): REST と gRPC が通る**唯一の問い合わせ**。
    //
    // 🔴 **2 つの輸送で同じ関数を通す。** 写すと、片方だけ順序や射影が変わった状態が作れる ——
    // 本リポジトリが繰り返し踏んでいる形である（直近では #1330 が 5 巡かけて潰した）。
    // 🔴 **名前順は契約である**（呼び出し元は集合へ落とすので順序を使わないが、面の側で決めておく）。
    // 🔴 **使用件数を射影しない**（管理面の集計であり生成に要らない。ADR-0043 決定 1 / IADR-0364 決定 2）。
    internal static Task<List<string>> ReadNamesAsync(DocumentDbContext db, CancellationToken ct) =>
        db.Tags.OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(ct);
}
