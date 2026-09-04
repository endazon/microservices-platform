using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Contracts.Dtos;
using System.Net.Http.Json;

namespace Platform.Shared.Infrastructure.Foundation.Authz;

// FR-05, UC-01, IADR-0009: BFF 集約点での ABAC スコープ解決（横断検索・文書閲覧の共通前段）。
// JWT からサーバ側で利用者を特定し、AuthorizationService でスコープを解決する。クライアントが
// 指定した Scope は信頼しない（権限昇格の防止）。deny-by-default: 許可ポリシーが無い／解決不能な
// 場合は null（＝閲覧可能なし）へ縮退する。呼び出し側は null を「空応答」または「404 秘匿」へ写す。
public static class BffScopeResolver
{
    // 利用者の許可スコープを解決する。許可（Granted=true）のときのみ BffAccessScope を返し、
    // それ以外（未マッチ・認可サービス不調）は null を返す。
    //
    // FR-05, FR-06, ADR-0036 D-07, IADR-0272 決定 4 (#1010): **action は既定値の無い必須引数である。**
    // #1010 は「/authz/scope が read を返すこと」を暗黙の前提にした呼び出しが BFF の書き込み経路へ
    // 効いていた欠陥（#993 の platform 共通版）。既定値を残すと、新しい経路を足した人が書き忘れる
    // ことで認可が緩む。既定値を外せばアクションの選択がコンパイラに強制される。
    // 読み取りは BffScopeAction.Read、作成・更新・削除は BffScopeAction.Write を渡す。
    //
    // FR-19, ADR-0036, IADR-0253 決定 1（段 3・BFF の分岐対応 / #989）: 応答の Branches
    // （名前つき分岐）をそのまま運ぶ。BFF 内の判定には BffAccessScope を用い、
    // 後段へ渡すときは ToContractScope() で契約型へ写す（**Branches も運ばれる**。段 3 完了）。
    //
    // NFR-09, ADR-0029, ADR-0075, IADR-0379 (#1201): **gRPC 経路（参照実装）との並走。**
    // `Services:AuthorizationServiceGrpc` が構成されて AuthzScopeGrpcClient が DI に在れば gRPC で解決し、
    // 無ければ従来どおり REST で解決する。**並走中の正は REST**（gRPC は opt-in）。どちらの経路も
    // 同じ deny-by-default（null）へ縮退する。利用者の JWT は gRPC のメタデータへ載せない ——
    // 載せるのは BFF 自身の s2s トークンであり、利用者の文脈は本文（userId / 属性 / action）で運ぶ。
    public static async Task<BffAccessScope?> ResolveAsync(
        IHttpClientFactory httpFactory, HttpContext http, string action, CancellationToken ct)
    {
        var userId = http.User.Identity?.Name ?? "anonymous";
        var userAttrs = ExtractUserAttributes(http);

        if (http.RequestServices?.GetService<AuthzScopeGrpcClient>() is { } grpc)
            return await grpc.ResolveAsync(userId, userAttrs, action, ct);

        var authzClient = httpFactory.CreateClient("AuthorizationService");
        try
        {
            var scopeResp = await authzClient.PostAsJsonAsync("/authz/scope",
                new AccessScopeRequest(userId, userAttrs, action), ct);
            var resolved = scopeResp.IsSuccessStatusCode
                ? await scopeResp.Content.ReadFromJsonAsync<AccessScopeResponse>(ct)
                : null;

            // deny-by-default: 許可ポリシーが無い/解決不能 → 閲覧可能なし。
            if (resolved is not { Granted: true })
                return null;

            return new BffAccessScope(resolved.AllowedFilters, resolved.Granted, resolved.Branches);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 認可サービス不調は deny-by-default（null）へ縮退する。
            return null;
        }
    }

    // FR-05: JWT から ABAC 判定に用いる利用者属性を取り出す（検索・分析と同一の属性キー）。
    public static Dictionary<string, string> ExtractUserAttributes(HttpContext ctx)
    {
        var attrs = new Dictionary<string, string>();
        var clearance = ctx.User.FindFirst("clearance")?.Value;
        var department = ctx.User.FindFirst("department")?.Value;
        if (clearance is not null) attrs["clearance"] = clearance;
        if (department is not null) attrs["department"] = department;
        return attrs;
    }

    // FR-05: 単一文書の属性がスコープに合致するか判定する（AbacEvaluator と同一意味論）。
    // フィルタ間は AND、値集合内は OR。Filters が空かつ GrantsAccess=true は「条件なしで全件許可」。
    //
    // FR-19, ADR-0036, ADR-0046 D-06 部品 3, IADR-0253 決定 1（段 3・BFF の分岐対応 / #989）:
    //   Branches が 1 件以上あれば **いずれかの分岐のフィルタをすべて満たす文書が一致**
    //   （分岐内 AND・分岐間 OR）。1 分岐 = マッチした 1 ポリシーの文書条件であり、計画の read 規則
    //   「静的属性ベース ∨ 所有者ベース ∨ 共有先ベース」の選言を写す（WikiService の
    //   AbacPageFilter と同一意味論）。分岐のフィルタが空 =（そのポリシーの範囲で）全件許可。
    //   Branches が空/null なら従来どおり Filters で評価する（後方互換。未移行の応答）。
    //   ${current_user} は認可サービスが分岐の中で束縛済みであり、ここでは解釈しない
    //   （IADR-0253 決定 3 —— 述語が解釈すると認可の判断が 2 箇所へ散る）。
    //
    // 🔴 **分岐をキー単位 union で 1 本に潰す形へ戻してはならない**（IADR-0253 決定 2 の
    //   2026-08-23 追記）。union は「どのポリシー単独も許可しない値の混成」を許す
    //   （A=internal×hr と B=public×sales から internal×sales が通る）—— 漏れる向きの乖離である。
    public static bool Matches(IReadOnlyDictionary<string, string> attributes, BffAccessScope scope)
    {
        if (!scope.GrantsAccess)
            return false;

        // #989 段 3: 分岐があれば選言で評価する（分岐間 OR・分岐内 AND）。
        if (scope.Branches is { Count: > 0 })
            return scope.Branches.Any(b => MatchesAll(attributes, b.Filters));

        return MatchesAll(attributes, scope.Filters);
    }

    // フィルタ間 AND、値集合内 OR。属性キーを持たない文書は不一致（欠落は安全側に倒す）。
    private static bool MatchesAll(
        IReadOnlyDictionary<string, string> attributes, List<AttributeFilter> filters)
    {
        foreach (var filter in filters)
        {
            if (!attributes.TryGetValue(filter.Key, out var value))
                return false;
            if (!filter.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}

// FR-05, FR-19, IADR-0253 決定 1・2（段 3 / #989）: BFF 内の判定に用いる解決済みスコープ。
// BFF は認可応答の全体（Filters ＋ Granted ＋ Branches）を扱うため、契約型より広い資料型を持つ。
//
// Filters は従来の算出値（キー単位 union の連言）そのまま —— 値も意味も変えない（IADR-0253 決定 2）。
//
// ［2026-08-28 追記 / #989 段 3］**ToContractScope() は Branches を運ぶようになった。**
// 波 1（#1010 / #989 BFF 分）では契約型 AccessScope が Branches を持たなかったため、後段
// （RetrievalService）へ渡す時点で落としていた。その留保は「**消費側が未移行だから**」を理由に
// した意図的な先送りであり、段 3 で Retrieval / AiAnalysis / Graph の移行が完了したことに伴い、
// 契約 AccessScope へ Branches を足して（末尾・既定値付き＝非破壊）運ぶ形へ反転した。
// **落としたままにすると「BFF は分岐で判定するが後段は従来評価」という食い違いが残り、
// 検索経路だけが混成を許す**（IADR-0253 決定 2 の反例）。
public sealed record BffAccessScope(
    List<AttributeFilter> Filters,
    bool GrantsAccess,
    List<AccessScopeBranch>? Branches = null)
{
    // 後段へ渡す契約型への写し。**Branches も運ぶ**（段 3 完了。上の追記を参照）。
    public AccessScope ToContractScope() => new(Filters, GrantsAccess, Branches);
}

// FR-05, FR-21, ADR-0036 D-07, IADR-0253 決定 5, IADR-0272 決定 4 (#1010):
// BFF が解決するアクションの語彙。
//
// **値域の正本は AuthorizationService の `PolicyAction`**（read / analyze / manage / write）だが、
// 共有基盤（Platform.Shared.Infrastructure）からサービスプロジェクトは参照できず、契約 DTO
// （Platform.Shared.Contracts）は値域を持たない（既定値のリテラル "read" だけを持つ）。
// したがってここに写しを置く（GraphService の GraphAccessAction と同じ形・同じ理由）。
//
// **綴りがずれても緩む向きには壊れない** —— /authz/scope は値域外を 400 で返し、
// BffScopeResolver は非 2xx を null（deny-by-default）へ縮退させる。
public static class BffScopeAction
{
    // 閲覧・検索・属性値照会（存在秘匿つきの読み取り経路）。
    public const string Read = "read";

    // 作成・更新・削除（ADR-0036 D-07: doc.owner ∈ { ${current_user} }）。
    public const string Write = "write";
}
