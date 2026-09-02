using Knowledge.Bff.Endpoints.Usage;
using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-03, FR-05, UC-01, SC-01: BFF 検索集約エンドポイント。
// 横断検索は「ABAC スコープ解決（AuthorizationService）→ ハイブリッド検索（RetrievalService）」を集約する。
// スコープはサーバ側で JWT から解決し、クライアント指定の Scope は信頼しない（権限昇格の防止）。
// deny-by-default: 許可ポリシーが無ければ空を返す（権限外の存在を示さない・IADR-0009 と整合）。
public static class SearchBffEndpoints
{
    // FR-03: 既定・上限の TopK（過大要求を抑止）。
    private const int DefaultTopK = 10;
    private const int MaxTopK = 50;

    public static IEndpointRouteBuilder MapSearchBffEndpoints(this IEndpointRouteBuilder app)
    {
        // NFR-09, #656: **認証を要求する。** 計画の暫定運用は「エッジ（BFF）で OIDC/JWT を担保する」と
        // 定めており、本群はそれを満たしていなかった（無認証で到達できた）。
        // **ロールは要求しない** —— 計画 `05_screens` は利用者グループ（SC-01〜04・SC-08）を
        // 「ABAC の権限内で全利用者が利用できる」と定めており、書かれていない制限を足さない。
        // 無認証でも ABAC が fail-closed で空を返していたが、**それは防御が 1 枚しかない状態**である
        // （利用者条件を持たないポリシーが 1 件でも入ると開く。IADR-0044 の多層防御）。
        var g = app.MapGroup("/bff/search").WithTags("Search BFF").RequireAuthorization();

        // FR-03, UC-01: 横断検索。要求は query（＋任意 topK）。Scope はクライアントから受け取らずサーバで解決する。
        g.MapPost("/", async (
            SearchRequest req,
            IHttpClientFactory httpFactory,
            IUsageEventReporter usage,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query))
                return Results.Ok(new SearchResponse([], 0, 0));

            var topK = Math.Clamp(req.TopK <= 0 ? DefaultTopK : req.TopK, 1, MaxTopK);

            // FR-05: 利用者の ABAC 許可スコープをサーバ側で解決する（deny-by-default。クライアント指定
            // Scope は信頼しない）。許可ポリシーが無い／認可サービス不調は空応答へ縮退する（存在秘匿）。
            // #1010: 検索は読み取り経路 → read を明示する。
            var scope = await BffScopeResolver.ResolveAsync(httpFactory, http, BffScopeAction.Read, ct);
            if (scope is null)
                return Results.Ok(new SearchResponse([], 0, 0));

            // FR-03: 解決済みスコープでハイブリッド検索を実行する（クライアント指定 Scope は使わない）。
            var retrievalClient = httpFactory.CreateClient("RetrievalService");

            // FR-05, FR-17, ADR-0034, ADR-0035 (#970): 🔴 **利用者の `Authorization` をそのまま
            // 後段へ伝播する（方式 A）。** 二段検索の段（グラフ近傍展開）はこのヘッダを
            // RetrievalService → GraphService と運んでホップごと ABAC を効かせる —— 伝播しないと
            // 段を有効化しても展開は常に 0 件だった（IADR-0263 残件 2）。**無ければ付けない**
            // （縮退の判断とその警告は RetrievalService 側が一元で持つ）。
            var auth = http.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(auth))
                retrievalClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
            try
            {
                // #531: 検索モードは利用者の指定をそのまま透過する（Scope と違い信頼性の問題が無い——
                // モードは絞り込みの種類であって権限ではない）。未知値の縮退は RetrievalService 側が行う。
                var searchResp = await retrievalClient.PostAsJsonAsync("/search",
                    // FR-03, SC-02（#531 / #532）: 検索モードと並び順は**利用者の指定をそのまま後段へ渡す**。
                    // 縮退（未知値 → 既定）は RetrievalService が一箇所で行う（BFF で二重に正規化しない）。
                    // #989 段 3: 契約型へ写して渡す。**Branches も運ばれる**（段 3 完了）——
                    // 後段 RetrievalService が分岐間 OR で評価する。
                    new SearchRequest(req.Query, topK, req.AttributeFilters, scope.ToContractScope(),
                        req.Mode, req.SortBy), ct);
                if (!searchResp.IsSuccessStatusCode)
                    return Results.StatusCode((int)searchResp.StatusCode);

                var result = await searchResp.Content.ReadFromJsonAsync<SearchResponse>(ct);

                // FR-10, SC-10, [[IADR-0336]] (#1103): **利用状況イベント（search）を発火する。**
                // ここまで来たときだけ数える —— 空クエリ・許可スコープ無し・後段の非 2xx は
                // 上で早期に返っており、**実行されていない検索を利用状況に数えない**。
                //
                // 🔴 **応答を待たない**（`Report` は列へ載せるだけで例外も投げない）。検索の
                // 応答時間（NFR 検索 p95 1.5s）に計測の往復を載せず、計測の失敗で検索を落とさない。
                // 送るのは種別と検索語だけで、利用者は受け口が `Authorization` から解決する。
                usage.Report(new UsageEventSignal(UsageEventType.Search, req.Query, auth));

                return Results.Ok(result ?? new SearchResponse([], 0, 0));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return Results.Ok(new SearchResponse([], 0, 0));
            }
        }).WithName("BffSearch").Produces<SearchResponse>();

        // FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（計画 ADR-0043・裁定 Q2）。
        // SC-01 / SC-08 の対象範囲フィルタが「**権限内のタグ／部門／プロジェクトのみ選択可**」を
        // 満たすための候補一覧である。**一般利用者が呼べる**（従前は管理者限定の辞書しか無かった）。
        //
        // **スコープはサーバ側で解決し、クライアント指定は信頼しない**（検索と同じ。権限昇格の防止）。
        // **解決できないときは空配列**（[[IADR-0151]] 決定 5）——404 にも 403 にもしない。
        // **候補が無いことと権限が無いことを利用者へ区別させない。**
        // NFR-09, #656: 同上（認証のみ・ロール不問）。SC-01 / SC-08 の対象範囲フィルタが呼ぶ口である。
        var values = app.MapGroup("/bff/attribute-values").WithTags("Search BFF").RequireAuthorization();

        values.MapPost("/", async (
            AttributeValuesRequest req,
            IHttpClientFactory httpFactory,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Key))
                return Results.Ok(new AttributeValuesResponse([]));

            // FR-09, SC-05, SC-09, #634: 管理者・運用者には**辞書そのもの**を添える（IADR-0152 決定 3）。
            // **口は 1 系統のまま**（ADR-0043 決定 4「読み取り口を 3 種類作らない。スコープだけロール別」）。
            // **一般利用者には `null` のまま**で、`Values` の形は #540 から変わらない。
            // 一般利用者へ辞書を返してはならない —— ADR-0043 決定 1 が明示的に禁じている
            // （権限外の文書に固有の値からその存在が推測できる）。
            TagDictionaryResponse? dictionary = null;
            if (IsTagKey(req.Key) && IsDictionaryReader(http))
            {
                var (dict, failure) = await FetchTagDictionaryAsync(httpFactory, http, ct);
                // 後段の非 2xx は透過する（辞書を引けるのは管理者・運用者だけなので一般利用者に影響しない）。
                if (failure is not null) return failure;
                dictionary = dict;
            }

            // #1010: 属性値の照会は読み取り経路 → read を明示する。
            var scope = await BffScopeResolver.ResolveAsync(httpFactory, http, BffScopeAction.Read, ct);
            if (scope is null)
                return Results.Ok(new AttributeValuesResponse([], dictionary));

            var retrievalClient = httpFactory.CreateClient("RetrievalService");
            try
            {
                // **クライアントが送ってきた Scope は使わない**（解決済みで置き換える）。
                // #989 段 3: 契約型へ写して渡す。**Branches も運ばれる**（段 3 完了）——
                // 後段の値集合照会も分岐間 OR で絞られる（候補と検索の一致。IADR-0151 決定 1）。
                var resp = await retrievalClient.PostAsJsonAsync("/search/attribute-values",
                    new AttributeValuesRequest(req.Key, scope.ToContractScope()), ct);
                if (!resp.IsSuccessStatusCode)
                    return Results.StatusCode((int)resp.StatusCode);

                var result = await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(ct);
                // 後段は辞書を知らない。**辞書は BFF がここで添える**（IADR-0152 決定 3）。
                return Results.Ok(new AttributeValuesResponse(result?.Values ?? [], dictionary));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // **後段へ到達できないときだけ**空配列へ縮退する（検索と同じ扱い。存在秘匿を崩さない）。
                // **後段が返した非 2xx は上で透過済み**である —— 障害を 200 空応答で隠さない。
                return Results.Ok(new AttributeValuesResponse([], dictionary));
            }
        }).WithName("BffAttributeValues").Produces<AttributeValuesResponse>();

        return app;
    }

    // FR-09, SC-05, SC-09, #634: 辞書が在るのは `tags` だけである。
    // ABAC 属性（`department` 等）の許可値は `AttributeDefinition.AllowedValues` が持っており、
    // **タグ辞書ではない**（計画も同じ切り分けをしている）。
    private static bool IsTagKey(string key) =>
        string.Equals(key, AttributeValueKeys.Tags, StringComparison.OrdinalIgnoreCase);

    // FR-09, SC-05, #634: 辞書を読めるのは管理者・運用者である（裁定 Q18。IADR-0152 決定 5）。
    // **ここで塞ぐのは早期の門であり、実効境界は DocumentService 側である**（IADR-0044 多層防御 /
    // IADR-0039 決定 2）。BFF を迂回されても後段の `/tags` が同じロールを要求する。
    private static bool IsDictionaryReader(HttpContext http) =>
        http.User.IsInRole(PlatformAuthPolicies.AdminRole)
        || http.User.IsInRole(PlatformAuthPolicies.OperatorRole);

    // FR-09, SC-05, SC-09, #634: DocumentService からタグ辞書（値集合 ＋ 使用件数）を取る。
    // **利用者の資格情報を後段へ引き継ぐ**（後段が最終防衛線として同じロールを検査する）。
    // 戻り値は (辞書, 透過すべき失敗応答) で、**どちらか一方だけが非 null** である。
    private static async Task<(TagDictionaryResponse? Dictionary, IResult? Failure)> FetchTagDictionaryAsync(
        IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("DocumentService");
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

        try
        {
            var resp = await client.GetAsync("/tags", ct);
            if (!resp.IsSuccessStatusCode)
                return (null, Results.StatusCode((int)resp.StatusCode));

            return (await resp.Content.ReadFromJsonAsync<TagDictionaryResponse>(ct), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // **後段へ到達できないときは辞書を添えずに続ける** —— 候補一覧（`Values`）は
            // 辞書に依存しないので、辞書が引けないことで検索の候補まで落とさない。
            return (null, null);
        }
    }
}
