using FluentValidation;
using GraphService.Domain;
using GraphService.Domain.Ports;
using Platform.Shared.Kernel;

namespace GraphService.Features.Graph.Neighbors;

// FR-17, UC-10, ADR-0034 決定 1・2・3・4: 近傍探索（多ホップ）。
//
// 🔴 **hops 上限の超過は 400 で拒否する。黙って切り詰めない**（決定 3）。
// 切り詰めると、利用者は「3 ホップ先まで見た」と思い込んだまま欠けた結果を受け取る。
//
// 🔴 **本ハンドラの判定順は仕様である。** `hops` / `types` の検証は認可より前に置く
// （下の注記を参照）。ファイルを移しても順序を組み替えてはならない。
// `GraphEndpointsSecrecyTests` がこの帰結（本文・ヘッダで区別できないこと）を固定する。
internal static class GraphNeighborsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{documentId:guid}/neighbors", async (
            Guid documentId,
            int? hops,
            // FR-17, SC-18, ADR-0049 決定 4 (#980): 間引きの基準（distance 既定 / updated / degree）。
            // **未知の値・未指定は既定へ縮退する**（例外にしない。SearchModes / SearchSorts と同じ作法）
            // —— 綴りを 1 つ間違えただけで画面が壊れる形にしない。
            string? by,
            // FR-17, SC-18 (#917): 辺の型フィルタ（型 ID のカンマ区切り。未指定・空 = 絞らない）。
            // サーバ側で絞るのが仕様である（planning#446。クライアントで打ち切り後に絞ると範囲が狭まる）。
            string? types,
            IValidator<NeighborsQuery> validator,
            IGraphAccessResolver accessResolver,
            IGraphStore store,
            GraphTraversal traversal,
            HttpContext http,
            CancellationToken ct) =>
        {
            // 🔴 **hops と types の検証は認可より前に置く。順序を入れ替えてはならない。**
            //
            // 入れ替えると存在秘匿が壊れる —— 認可を先にすると、権限外・不存在の文書は 404、
            // 可視の文書だけが 400 を返すようになり、**hops=4 を投げるだけで文書の存在が判る**。
            // hops と types の妥当性は文書に依存しないので、先に弾けば何も漏れない。
            //
            // ⚠ CodeQL の `cs/user-controlled-bypass` がここを high で指摘する（利用者入力が
            // 条件を制御しているため）。**バイパスではない** —— この分岐は要求を*拒否*するだけで、
            // 通過した場合の認可（スコープ解決・Authorize）は無条件に実行される。
            // 指摘に従って順序を変えると、上記のとおり実際の情報漏れを作ることになる。
            //
            // 🔴 **`IValidator<T>` がハンドラの引数にあることは順序の証拠にならない**（引数は解決で
            // あって実行ではない）。**順序を決めているのはこの行の位置である**（IADR-0395 決定 2）。
            //
            // FR-17 / IADR-0371 決定 2・4 / IADR-0395 決定 4: 検証の失敗を Kernel の `Result` で表し、
            // **HTTP への写像は 1 度だけ行う**。本文が 2 欄なので `Error.Code` を `error` へ、
            // `Error.Message` を `message` へ写す（1 欄の端点はこの形を使わない）。
            var gate = Validate(validator, new NeighborsQuery(hops, types));
            if (gate.IsFailure)
                return Results.BadRequest(new { error = gate.Error.Code, message = gate.Error.Message });

            var requested = hops ?? GraphTraversal.DefaultHops;

            // FR-17, SC-18 (#917): 辺の型フィルタの**解析**。検証は上で済んでいる（IADR-0395 決定 5
            // で検証と解析を分けた）ので、ここへ到達した `types` は全要素が GUID として読める。
            // **実在しない型 ID は拒まない** —— 辺の型辞書は認証のみで全利用者へ公開済みの語彙であり
            // （#962）、実在の有無は秘匿対象ではなく、単に 1 本も一致しないだけである
            // （辞書の改廃と URL の共有が競合しても壊れない）。
            //
            // 🔴 **1 件も読めなければ `null`（＝絞らない）へ縮退する**（`types=",,,"` は 400 ではない）。
            // 移送前と同じ縮退である。
            IReadOnlySet<Guid>? edgeTypes = null;
            if (!string.IsNullOrWhiteSpace(types))
            {
                var parsed = new HashSet<Guid>();
                foreach (var part in types.Split(',', NeighborsQueryValidator.TypesSplitOptions))
                    parsed.Add(Guid.Parse(part));
                if (parsed.Count > 0)
                    edgeTypes = parsed;
            }

            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);
            if (!scope.Granted)
                return GraphEndpoints.NotFound();

            var start = await store.FindNodeAsync(documentId, ct);
            if (start is null)
                return GraphEndpoints.NotFound();

            var origin = AuthorizedNode.Authorize(start, scope);
            if (origin is null)
                return GraphEndpoints.NotFound();

            var subgraph = await traversal.ExploreAsync(origin, scope, requested, by, edgeTypes, ct);

            return Results.Ok(GraphViewResponse.Seal(subgraph, scope));
        }).WithName("GetGraphNeighbors")
          .RequireAuthorization()
          .Produces<GraphViewResponse>()
          .Produces(StatusCodes.Status400BadRequest)
          .Produces(StatusCodes.Status404NotFound);
    }

    // FR-17 / IADR-0371 決定 2: 入力規則の判定。**規則そのものは `NeighborsQueryValidator` が持つ。**
    //
    // 🔴 **`Errors[0]` を採る。** FluentValidation は既定で全規則を走らせるため、
    // 移送前の「最初の違反で 400 を返す」と同じ本文にするには最初の失敗を採るしかない。
    // 規則の宣言順が応答の契約の一部になっている（同 Validator のコメントを参照）。
    private static Result Validate(IValidator<NeighborsQuery> validator, NeighborsQuery query)
    {
        var result = validator.Validate(query);
        return result.IsValid
            ? Result.Success()
            : Result.Failure(Error.Validation(
                result.Errors[0].ErrorCode, result.Errors[0].ErrorMessage));
    }
}
