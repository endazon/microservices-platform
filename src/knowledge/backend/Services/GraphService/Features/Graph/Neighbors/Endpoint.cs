using GraphService.Domain;
using GraphService.Domain.Ports;

namespace GraphService.Features.Graph.Neighbors;

// FR-17, UC-10, ADR-0034 決定 1・2・3・4: 近傍探索（多ホップ）。
//
// 🔴 **hops 上限の超過は 400 で拒否する。黙って切り詰めない**（決定 3）。
// 切り詰めると、利用者は「3 ホップ先まで見た」と思い込んだまま欠けた結果を受け取る。
//
// 🔴 **判定順は仕様である。** `hops` / `types` の検証は認可より前に置く。
// ［2026-09-07 追記 / #1255］**順序を持っている場所は `ExpandNeighborsUseCase` へ移った**
// （計画 `ADR-0086` 決定 1 に従い、east-west gRPC 面と本体を共有したため）。
// **本ハンドラは輸送の写しだけを持つ。** ファイルを移しても順序を組み替えてはならない。
//
// **この順序を固定するのは以下の 6 本である**（#1248 追随で数え直した。
// 従前この位置は `GraphEndpointsSecrecyTests` だけを指していたが、**同クラスに neighbors を
// 触る試験は 1 本も無かった** —— 指し先が空だった）:
//
// 権限の無いスコープでも 400 が返ることで見るもの（認可を先にすると 404 になって落ちる）:
//   - `GraphValidationResponseContractTests.Neighbors_HopsOutOfRange_Returns400WithBothFields`
//   - `GraphValidationResponseContractTests.Neighbors_InvalidTypes_Returns400WithBothFields`
//   - `TwoTierTraversalTests.Hops_out_of_range_is_still_rejected_before_authorization`
//
// 不存在の文書 ID でも 400 が返ることで見るもの:
//   - `EdgeTypeFilterTests.Malformed_types_returns_400_even_for_a_nonexistent_document`
//   - `GraphTraversalTests.Hops_validation_does_not_leak_document_visibility`（可視と不存在の対）
//
// **実在するが不可視**の文書との対で見るもの（上の 5 本はこの腕を持たない）:
//   - `GraphEndpointsSecrecyTests.Neighbors_validation_runs_before_authorization_for_visible_and_hidden_alike`
//
// 検証を通った先の 404 が**本文・ヘッダで区別できない**ことは
// `GraphEndpointsSecrecyTests.Neighbors_unauthorized_missing_and_nonexistent_are_indistinguishable`
// が固定する（#1248 追随で新設。それまで neighbors の 404 の区別不能性は無検査だった）。
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
            ExpandNeighborsUseCase neighbors,
            HttpContext http,
            CancellationToken ct) =>
        {
            // FR-05, FR-17, NFR-09, 計画 ADR-0086 決定 1, [[IADR-0410]] (#1255):
            // 🔴 **本体は `ExpandNeighborsUseCase` である。REST と east-west gRPC が同じ関数を通る。**
            // 検証と認可の順序（検証が先）・存在秘匿・出力ゲートはすべて本体側にあり、
            // ここに在るのは「輸送の言葉へ写すこと」だけである。
            //
            // 🔴 **順序の試験（6 本）は REST を通して同じ順序を観測し続ける** ——
            // 移送で順序が変わっていないことは、それらが赤くならないことで確かめる。
            var outcome = await neighbors.ExecuteAsync(
                documentId, hops, by, types, GraphUserContext.FromHttpContext(http), ct);

            // FR-17 / [[IADR-0371]] 決定 2・4 / [[IADR-0395]] 決定 4: 検証の失敗を Kernel の `Result` で表し、
            // **HTTP への写像は 1 度だけ行う**。本文が 2 欄なので `Error.Code` を `error` へ、
            // `Error.Message` を `message` へ写す（1 欄の端点はこの形を使わない）。
            if (outcome.IsFailure)
                return Results.BadRequest(new { error = outcome.Error.Code, message = outcome.Error.Message });

            return outcome.Value.View is { } view
                ? Results.Ok(view)
                : GraphEndpoints.NotFound();
        }).WithName("GetGraphNeighbors")
          .RequireAuthorization()
          .Produces<GraphViewResponse>()
          .Produces(StatusCodes.Status400BadRequest)
          .Produces(StatusCodes.Status404NotFound);
    }
}
