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
