namespace LlmGateway.Domain.Routing;

// FR-02, FR-05, ADR-0016, 08_data-egress-policy: 埋め込み専用の越境ポリシー（送信先ティア）。
//
// 埋め込みは取り込み時に「文書本文の全量」を送信するため、RAG 質問時の LLM 呼び出し（プロンプトの一部）より
// データ露出が大きい。したがって一般 LLM 越境（EgressMatrix）より厳格にし、confidential/restricted は
// ティアA（セルフホスト＝社外送信なし）に固定する（EgressMatrix は confidential にティアB も許容していたが、
// 埋め込みでは許容しない）。ADR-0016 の「高機密区分の埋め込みはセルフホストに固定」を実装する。
public static class EmbeddingEgress
{
    // 機密区分ごとに埋め込み送信を許容するティア集合。
    public static IReadOnlySet<ProtectionTier> AllowedTiers(SensitivityClass sensitivity) => sensitivity switch
    {
        // public / internal: ティアA（セルフホスト）・ティアB（保護契約済み外部＝voyage）を許容。既定は B（voyage）。
        SensitivityClass.Public => new HashSet<ProtectionTier> { ProtectionTier.A, ProtectionTier.B },
        SensitivityClass.Internal => new HashSet<ProtectionTier> { ProtectionTier.A, ProtectionTier.B },
        // confidential / restricted: ティアA（セルフホスト）のみ。外部（ティアB/C）へは本文を送らない。
        SensitivityClass.Confidential => new HashSet<ProtectionTier> { ProtectionTier.A },
        SensitivityClass.Restricted => new HashSet<ProtectionTier> { ProtectionTier.A },
        // 未知・未指定は安全側でセルフホストのみ（restricted 相当）。
        _ => new HashSet<ProtectionTier> { ProtectionTier.A }
    };
}
