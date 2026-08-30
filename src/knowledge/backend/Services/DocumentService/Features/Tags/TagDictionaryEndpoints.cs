using DocumentService.Features.Tags.Create;
using DocumentService.Features.Tags.Delete;
using DocumentService.Features.Tags.List;
using DocumentService.Features.Tags.Rename;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Tags;

// FR-06, FR-09, SC-05, SC-09, UC-03, UC-05, #634: タグ辞書（IADR-0152）の合成点。
//
// ADR-0065 決定 2: 各ユースケースの実体は `Features/Tags/<操作>/` に居る。
// **ここに残すのは、操作をまたいで共有されるもの**だけである —— 2 つの route group
// （読みの下限・書きの下限）。
//
// **辞書を DocumentService が持つのは、使用件数が文書の局所クエリになるためである**（IADR-0152 決定 1）。
// サービスを跨ぐと削除拒否の判定のたびに同期呼び出しが要り、
// 数え落としが「消してはいけないタグを消せる」事故になる。
//
// **［#635］改名・削除を足した。** #634 で先送りしたのは、保持方式を識別子参照へ移す前だと
// 改名と削除で前提が食い違うためである（表示名を複写したままの改名は既存文書へ追随しない）。
public static class TagDictionaryEndpoints
{
    public static IEndpointRouteBuilder MapTagDictionaryEndpoints(this IEndpointRouteBuilder app)
    {
        // IADR-0044: 多層防御。BFF を迂回した直接呼び出しでも認可を実効化する（サービスが最終防衛線）。
        //
        // **読み取りは管理者・運用者**（SC-05 の裁定 Q18「読み取り専用の照会口を管理者・運用者へ開く」）。
        // **一般利用者はここを引かない** —— 一般利用者の候補は Qdrant の facet 経由である
        // （ADR-0043 決定 1 が辞書を丸ごと返すことを禁じている。IADR-0152 決定 4）。
        var read = app.MapGroup("/tags").WithTags("Tags")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        // **追加はシステム管理者限定**（SC-09 のアクセス制御「システム管理者ロール限定」）。
        var write = app.MapGroup("/tags").WithTags("Tags")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        ListTagsEndpoint.Map(read);
        CreateTagEndpoint.Map(write);
        RenameTagEndpoint.Map(write);
        DeleteTagEndpoint.Map(write);

        return app;
    }
}
