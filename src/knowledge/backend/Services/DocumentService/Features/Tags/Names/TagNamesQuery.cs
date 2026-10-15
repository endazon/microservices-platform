using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.Tags.Names;

// FR-18, SC-09, ADR-0063 決定 2, IADR-0364 決定 2 (#1014): **辞書の名前だけ**を返す問い合わせ。
//
// GraphService が AI 提案の**生成段**で「LLM に選ばせる値集合」として引く（辺の型辞書と同じ形）。
// 生成は利用者スコープで走るが、**タグ辞書の照会口（`/tags`）は管理者・運用者限定**（SC-05 Q18）で
// あり、利用者の資格で引くと一般利用者の生成が全件 403 で 0 件になる。**読み取り主体は
// GraphService 自身**であり、辞書はプロンプトへ入るだけで利用者へ丸ごとは返らない。
//
// ［2026-10-15 / #1902・[[IADR-0533]] 追記］無認証の REST の受け口（`GET /internal/tags/names`。旧 `TagNamesEndpoint`）は
// 撤去した。読み手（GraphService の D-3）は gRPC `TagDictionary/ListNames`（`ServiceCaller`）だけになっていた。
// 本体はここに残し、gRPC 面（`TagDictionaryGrpcService`）が呼ぶ。
//
// 🔴 **使用件数を返さない。** 件数は管理面の集計値であり、生成には要らない。
public static class TagNamesQuery
{
    // FR-18, ADR-0063 決定 2, [[IADR-0412]] (#1255): 名前の**唯一の問い合わせ**（撤去前は REST と gRPC の両方が通っていた）。
    //
    // 🔴 **輸送の側に写さない。** 写すと、片方だけ順序や射影が変わった状態が作れる ——
    // 本リポジトリが繰り返し踏んでいる形である（直近では #1330 が 5 巡かけて潰した）。
    // 🔴 **名前順は契約である**（呼び出し元は集合へ落とすので順序を使わないが、面の側で決めておく）。
    // 🔴 **使用件数を射影しない**（管理面の集計であり生成に要らない。ADR-0043 決定 1 / IADR-0364 決定 2）。
    internal static Task<List<string>> ReadNamesAsync(DocumentDbContext db, CancellationToken ct) =>
        db.Tags.OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(ct);
}
