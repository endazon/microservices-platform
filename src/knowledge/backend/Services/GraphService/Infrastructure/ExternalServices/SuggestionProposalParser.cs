using System.Text.Json;
using GraphService.Domain;
using GraphService.Domain.Ports;

namespace GraphService.Infrastructure.ExternalServices;

// FR-18, ADR-0010, ADR-0034 決定 5, IADR-0266 決定 6・7, IADR-0398 (#1255):
// LLM 応答本文（JSON 配列）から提案を読む**共通の読み取り**。
//
// 🔴 REST 実装（LlmGatewaySuggestionClient）と gRPC 実装（LlmGatewayGrpcSuggestionClient）が
// **同じここを呼ぶ**。読み取りを 2 つに分けると、「どちらの輸送で来たか」で採れる提案が変わる ——
// 提案の妥当性は輸送の性質ではない。
internal static class SuggestionProposalParser
{
    // FR-18: 応答本文（JSON 配列）を読む。**読めなければ空**（例外を投げない）——
    // 生成の失敗は「提案が付かない」で足り、利用者の要求を落とす理由にならない。
    public static IReadOnlyList<LlmSuggestionProposal> Parse(string? text, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // モデルが前置きを付けた場合に備え、最初の '[' から最後の ']' までを採る。
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            return [];

        try
        {
            var wire = JsonSerializer.Deserialize<List<ProposalWire>>(
                text[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (wire is null)
                return [];

            var result = new List<LlmSuggestionProposal>();
            foreach (var w in wire)
            {
                var kind = w.Kind?.Trim().ToLowerInvariant();
                if (kind is null || !SuggestionKind.IsValid(kind))
                    continue;
                result.Add(new LlmSuggestionProposal(
                    kind, w.TargetDocumentId, w.EdgeTypeName, w.TagValue, w.Rationale ?? string.Empty));
            }
            return result;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "LLM gateway returned an unparsable suggestion payload");
            return [];
        }
    }

    private sealed record ProposalWire(
        string? Kind, Guid? TargetDocumentId, string? EdgeTypeName, string? TagValue, string? Rationale);
}
