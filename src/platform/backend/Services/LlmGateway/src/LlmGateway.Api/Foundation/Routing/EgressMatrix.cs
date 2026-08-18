namespace LlmGateway.Api.Foundation.Routing;

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

    // FR-11, IADR-0022, 08_data-egress-policy(注意点): ゼロデータ保持（ZDR）を要件とする機密区分か。
    // confidential/restricted は「ZDR を要件とする用途」とみなし、ZDR 非対応モデル（例: claude-fable-5）を
    // 候補から除外する。未知区分は安全側（ZDR 要求）に倒す。public/internal は要求しない。
    // ［2026-08-18 追記 / #850］計画 ADR-0038 決定 2 により claude-fable-5 は Models から外れ、本番設定
    // （appsettings.json）の NonZdrModels は**空**になった。除外機構そのものは残す —— 非 ZDR モデルを
    // 将来再び許可集合へ入れるときの唯一の統制点であり、単体カバレッジは LlmRouterTests の合成 config が持つ。
    public static bool RequiresZeroDataRetention(SensitivityClass sensitivity) => sensitivity switch
    {
        SensitivityClass.Public => false,
        SensitivityClass.Internal => false,
        SensitivityClass.Confidential => true,
        SensitivityClass.Restricted => true,
        _ => true
    };
}
