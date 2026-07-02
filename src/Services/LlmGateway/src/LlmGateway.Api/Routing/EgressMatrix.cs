namespace LlmGateway.Api.Routing;

// FR-11, ADR-0010, 08_data-egress-policy: 送信先のデータ保護ティア。
// A=セルフホスト（社外送信なし）, B=保護契約済み外部API（ZDR/学習不使用/レジデンシー）, C=標準外部API。
public enum ProtectionTier
{
    A = 0,
    B = 1,
    C = 2
}

// FR-11: 「機密区分 × 送信先ティア」越境マトリクス（08_data-egress-policy.md）。
public static class EgressMatrix
{
    // 機密区分ごとに送信を許容するティア集合。
    public static IReadOnlySet<ProtectionTier> AllowedTiers(SensitivityClass sensitivity) => sensitivity switch
    {
        // public: 全ティア可
        SensitivityClass.Public => new HashSet<ProtectionTier> { ProtectionTier.A, ProtectionTier.B, ProtectionTier.C },
        // internal: A/B 可、C は「条件付き可（要承認）」→ 候補には含め、要承認フラグで制御する
        SensitivityClass.Internal => new HashSet<ProtectionTier> { ProtectionTier.A, ProtectionTier.B, ProtectionTier.C },
        // confidential: A/B のみ（C 不可）
        SensitivityClass.Confidential => new HashSet<ProtectionTier> { ProtectionTier.A, ProtectionTier.B },
        // restricted: A/B のみ（B は追加統制下）、C 不可
        SensitivityClass.Restricted => new HashSet<ProtectionTier> { ProtectionTier.A, ProtectionTier.B },
        // 未知は安全側でセルフホストのみ
        _ => new HashSet<ProtectionTier> { ProtectionTier.A }
    };

    // ティアC送信に承認が必要か（internal × C のみ「条件付き可（要承認）」）。
    public static bool RequiresApproval(SensitivityClass sensitivity, ProtectionTier tier)
        => sensitivity == SensitivityClass.Internal && tier == ProtectionTier.C;
}
