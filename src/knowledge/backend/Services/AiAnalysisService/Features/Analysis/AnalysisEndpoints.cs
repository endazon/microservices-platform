using AiAnalysisService.Features.Analysis.Analyze;
using AiAnalysisService.Features.Analysis.Ask;
using AiAnalysisService.Features.Analysis.AskStream;

namespace AiAnalysisService.Features.Analysis;

// FR-04, FR-07, UC-01, UC-02: AI 分析・回答集約の登録表（ADR-0068 決定 1）。
//
// `MapGroup` とタグ付けは集約の全操作が使うものであり、特定の 1 操作に属さない。
// 各操作の処理は `Features/Analysis/<操作>/` に居る（ADR-0065 決定 2）。
// **ここに残すのは、操作をまたいで共有されるもの**だけである —— route group、
// 3 操作が共有する主体属性の取り出し、2 操作が共有する要求の形。
public static class AnalysisEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/analysis").WithTags("Analysis");

        AskEndpoint.Map(g);
        AskStreamEndpoint.Map(g);
        AnalyzeEndpoint.Map(g);

        return app;
    }

    // 🔴 FR-05, ADR-0004, ADR-0032, [[IADR-0009]], [[IADR-0044]], [[IADR-0335]] 決定 4 (#1318):
    // **未認証の要求は、認可サービスへ問い合わせる前に「閲覧できるものが無い」へ倒す。**
    //
    // 従前は未認証でも `http.User.Identity?.Name ?? "anonymous"` を身元として ABAC 判定へ進んで
    // いた。認可側（`AbacEvaluator`）は**利用者条件を持たないポリシーを全利用者にマッチさせる**ので、
    // そのようなポリシーが 1 件でも active なら**匿名にも許可が下りた**。fail-closed に
    // *見えていた*だけで、未認証時の応答がポリシーの内容次第で変わる ——
    // 固定されていない契約だった（Wiki で同型の欠陥を塞いだのが [[IADR-0335]] / #1126）。
    //
    // 🔴 **`!= true` である（`== false` ではない）。** `ClaimsPrincipal.Identity` は
    // 身元を 1 つも持たない主体で **null になり得る**。`== false` と書くと null が
    // 「匿名ではない」側へ落ち、**短絡をすり抜ける**（WikiAccessResolver と同じ綴り）。
    //
    // **401 にはしない。** エッジは BFF（ADR-0032 / Token Handler）であり、ここは mesh 内の後段で
    // ある（`/bff/analysis` 群が `RequireAuthorization()` を持ち、匿名はそこで 401 になる）。
    // 3 端点の匿名応答は**従来と同じ 200 ＋ 空回答**（`NoAccessAnswer`）のまま固定する ——
    // これは実物の `RagOrchestrator` が `Granted=false` のときに返していた値そのものであり、
    // **状態コードも本文も変えない。変わるのは「認可サービスを呼ばなくなる」ことだけである。**
    //
    // **3 操作すべてが使う**ため 2 段目に置く（ADR-0068 決定 2）。
    internal static bool IsAnonymous(HttpContext ctx)
        => ctx.User.Identity?.IsAuthenticated != true;

    // **3 操作すべてが使う**ため 2 段目に残る（ADR-0068 決定 2）。
    internal static Dictionary<string, string> ExtractUserAttributes(HttpContext ctx)
    {
        var attrs = new Dictionary<string, string>();
        var clearance = ctx.User.FindFirst("clearance")?.Value;
        var department = ctx.User.FindFirst("department")?.Value;
        if (clearance is not null) attrs["clearance"] = clearance;
        if (department is not null) attrs["department"] = department;
        return attrs;
    }
}

// FR-04, FR-05, SC-01, SC-08, #539: 対象範囲（属性フィルタ）。
//
// **`SearchRequest` は既に `AttributeFilters` を持つのに、こちらだけ持たなかった**
// （計画 L198・裁定 Q1 が「非対称を解消する」と定めた）。
//
// **多値である**（キー → 許可値の集合）。画面はチップを**複数**選ぶので単値では表現できない。
// `SearchRequest` 側の単値は自ら「後方互換」と名乗っている形であり、手本にしない。
//
// **範囲は narrowing-only である** —— ABAC 許可スコープと交差させ、**権限を一切広げない**
// （`DataRangeScopeResolver`）。クライアントが権限外の値を送っても結果は広がらない。
//
// 既定値つきで足すので**契約上は非破壊**である（[[IADR-0122]] 決定 2）。
//
// **`/ask` と `/ask/stream` の 2 操作が使う**ため 2 段目に残る（ADR-0068 決定 2）。
public record AskRequest(
    string Question,
    string? Scope = null,
    Dictionary<string, List<string>>? AttributeFilters = null);
