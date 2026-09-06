using FluentValidation;
using GraphService.Domain;
using GraphService.Domain.Ports;
using Platform.Shared.Kernel;

namespace GraphService.Features.Graph.Neighbors;

// FR-04, FR-05, FR-17, NFR-09, NFR-16, UC-10, SC-18, ADR-0004, ADR-0029, ADR-0034 決定 1〜4,
// ADR-0049 決定 4, ADR-0065 決定 2, ADR-0075, 計画 ADR-0086 決定 1・3, [[IADR-0242]], [[IADR-0272]],
// [[IADR-0371]] 決定 2, [[IADR-0379]] 決定 5, [[IADR-0395]], [[IADR-0410]] (#1255):
// **近傍探索の本体。**
//
// 🔴 **REST と east-west gRPC の両方がここを通る。判定器を 2 つにしない**
// （先行スライスの `EmbedUseCase` / `CompletionUseCase` / `DocumentReadUseCase` と同じ形。
// [[IADR-0397]] / [[IADR-0400]] / [[IADR-0402]]）。ハンドラ本体を写して 2 つ目の実装を作ると、
// **片方だけが直る形**になる —— 検証と認可の順序・存在秘匿・出力ゲートは
// **応答の意味**であって輸送の都合ではない。
//
// 🔴 **判定の順序は仕様である。** `hops` / `types` の検証は認可より**前**に置く。
// 入れ替えると存在秘匿が壊れる —— 認可を先にすると、権限外・不存在の文書は 404、
// 可視の文書だけが 400 を返すようになり、**hops=4 を投げるだけで文書の存在が判る**。
// hops と types の妥当性は文書に依存しないので、先に弾けば何も漏れない。
// **順序を決めているのは下の 2 行の位置である**（[[IADR-0395]] 決定 2 —— `IValidator<T>` が
// 引数にあることは順序の証拠にならない。引数は解決であって実行ではない）。
//
// ⚠ CodeQL の `cs/user-controlled-bypass` がこの分岐を high で指摘する。**バイパスではない** ——
// この分岐は要求を*拒否*するだけで、通過した場合の認可は無条件に実行される。
//
// 🔴 **「見えない」と「無い」を区別しない**（ADR-0034 決定 2）。スコープ無し・不存在・不可視は
// すべて `NeighborsOutcome.NotFound` である。REST はそれを 1 種類の 404 へ、gRPC は
// `found=false` へ写す。**区別を本体で作らないことが、区別できない 404 の構造的な保証**である。
//
// 🔴 **利用者文脈は引数で受け取る**（計画 `ADR-0086` 決定 1）。`HttpContext` を引数に取らない ——
// east-west gRPC 経路には `HttpContext.User` が無く（載っているのは呼び出し**元サービス**の
// s2s トークンである）、利用者文脈は要求本文で運ばれる。**判定の位置は動いていない** ——
// スコープを解決するのは依然として本サービスであり（`accessResolver`）、
// 呼び出し元が解決したスコープを信じる口はここに無い。
internal sealed class ExpandNeighborsUseCase(
    IValidator<NeighborsQuery> validator,
    IGraphAccessResolver accessResolver,
    IGraphStore store,
    GraphTraversal traversal)
{
    public async Task<Result<NeighborsOutcome>> ExecuteAsync(
        Guid documentId,
        int? hops,
        string? by,
        string? types,
        GraphUserContext user,
        CancellationToken ct = default)
    {
        // ★検証★ —— 認可より前。順序を組み替えてはならない（上の 🔴）。
        var gate = Validate(new NeighborsQuery(hops, types));
        if (gate.IsFailure)
            return Result<NeighborsOutcome>.Failure(gate.Error);

        var requested = hops ?? GraphTraversal.DefaultHops;

        // FR-17, SC-18 (#917): 辺の型フィルタの**解析**。検証は上で済んでいる（[[IADR-0395]] 決定 5
        // で検証と解析を分けた）ので、ここへ到達した `types` は全要素が GUID として読める。
        // **実在しない型 ID は拒まない** —— 辺の型辞書は認証のみで全利用者へ公開済みの語彙であり、
        // 実在の有無は秘匿対象ではなく、単に 1 本も一致しないだけである。
        //
        // 🔴 **1 件も読めなければ `null`（＝絞らない）へ縮退する**（`types=",,,"` は 400 ではない）。
        IReadOnlySet<Guid>? edgeTypes = null;
        if (!string.IsNullOrWhiteSpace(types))
        {
            var parsed = new HashSet<Guid>();
            foreach (var part in types.Split(
                NeighborsQueryValidator.TypesSeparator, NeighborsQueryValidator.TypesSplitOptions))
                parsed.Add(Guid.Parse(part));
            if (parsed.Count > 0)
                edgeTypes = parsed;
        }

        // ★認可★ —— **本サービスが自分で解決する**（ADR-0034 決定 1 のホップごと判定）。
        var scope = await accessResolver.ResolveForUserAsync(user, GraphAccessAction.Read, ct);
        if (!scope.Granted)
            return Result<NeighborsOutcome>.Success(NeighborsOutcome.NotFound);

        var start = await store.FindNodeAsync(documentId, ct);
        if (start is null)
            return Result<NeighborsOutcome>.Success(NeighborsOutcome.NotFound);

        var origin = AuthorizedNode.Authorize(start, scope);
        if (origin is null)
            return Result<NeighborsOutcome>.Success(NeighborsOutcome.NotFound);

        var subgraph = await traversal.ExploreAsync(origin, scope, requested, by, edgeTypes, ct);

        return Result<NeighborsOutcome>.Success(
            new NeighborsOutcome(GraphViewResponse.Seal(subgraph, scope)));
    }

    // FR-17 / [[IADR-0371]] 決定 2: 入力規則の判定。**規則そのものは `NeighborsQueryValidator` が持つ。**
    //
    // 🔴 **`Errors[0]` を採る。** FluentValidation は既定で全規則を走らせるため、
    // 移送前の「最初の違反で 400 を返す」と同じ本文にするには最初の失敗を採るしかない。
    // 規則の宣言順が応答の契約の一部になっている（同 Validator のコメントを参照）。
    private Result Validate(NeighborsQuery query)
    {
        var result = validator.Validate(query);
        return result.IsValid
            ? Result.Success()
            : Result.Failure(Error.Validation(
                result.Errors[0].ErrorCode, result.Errors[0].ErrorMessage));
    }
}

// FR-17, ADR-0034 決定 2, [[IADR-0410]] (#1255): 近傍探索の結果。
//
// 🔴 **`View` が `null` であることが「見えない・無い」の唯一の表現である。**
// スコープ無し・不存在・不可視を区別する項目を**足してはならない** —— 足した瞬間、
// 呼び出し側がその区別を応答に出せてしまい、存在秘匿が壊れる。
internal sealed record NeighborsOutcome(GraphViewResponse? View)
{
    public static NeighborsOutcome NotFound { get; } = new((GraphViewResponse?)null);

    public bool Found => View is not null;
}
