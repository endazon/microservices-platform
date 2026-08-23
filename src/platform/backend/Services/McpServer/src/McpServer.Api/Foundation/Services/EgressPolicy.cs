using McpServer.Api.Foundation.Contracts;

namespace McpServer.Api.Foundation.Services;

// FR-16, ADR-0024 §4: MCP クライアント側 LLM の データ保護水準ティア
// （計画 06_technical/08_data-egress-policy §送信先ティア）。
public enum EgressTier
{
    // ティアA: セルフホスト（社外送信なし）。
    SelfHosted = 0,

    // ティアB: 保護契約済み外部 API。
    ProtectedExternal = 1,

    // ティアC: 標準外部 API（保護契約が未確認）。
    StandardExternal = 2
}

// FR-16, UC-08 基本フロー 5・代替フロー, ADR-0024 §4:
// **ツール応答は外部 LLM へ渡る前提**で扱い、越境マトリクス（機密区分 × 送信先ティア）を
// **文書単位に**適用する。送信不可の文書は本文を返さず、参照リンクのみへ縮退する。
public sealed class EgressPolicy
{
    public const string ConfidentialityKey = "confidentiality";

    // 計画 08_data-egress-policy §越境マトリクス。
    //   public       : A 可 / B 可 / C 可
    //   internal     : A 可 / B 可 / C 条件付き可（要承認）→ **承認の仕組みが無い間は不可へ倒す**
    //   confidential : A 可 / B 可 / C 不可
    //   restricted   : A 可 / B 可（追加統制下）/ C 不可
    //
    // 🔴 **既定は安全側**（同 §基本原則）。未知の機密区分・区分の欠落は**送信不可**として扱う。
    // 「条件付き可（要承認）」を可へ倒さないのも同じ理由で、承認経路が実装されるまでは不可である。
    public static bool CanSendBody(string? confidentiality, EgressTier tier) => confidentiality switch
    {
        "public" => true,
        "internal" => tier <= EgressTier.ProtectedExternal,
        "confidential" => tier <= EgressTier.ProtectedExternal,
        "restricted" => tier <= EgressTier.ProtectedExternal,
        _ => tier == EgressTier.SelfHosted
    };

    // 送信不可の文書は本文を落とし、参照リンク（社内 Wiki URL）のみを残す。
    // **文書そのものは応答から消さない** —— 存在秘匿は ABAC の責務であり、越境統制は
    // 「見えてよい文書の本文を外へ出すか」を決める別の軸である（ADR-0024 §4 の縮退）。
    public McpToolResult Apply(EgressTier tier, McpToolResult result)
    {
        var documents = result.Documents.Select(d =>
        {
            d.Attributes.TryGetValue(ConfidentialityKey, out var confidentiality);
            return CanSendBody(confidentiality, tier) ? d : d with { Body = null };
        }).ToList();

        return result with { Documents = documents };
    }
}
