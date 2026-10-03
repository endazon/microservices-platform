namespace GraphService.Features.Clustering.Detect;

// FR-17, ADR-0035 決定 3, [[IADR-0496]] 決定 2・4 (#1733): クラスタ検出の日次バッチの**運用パラメータ**。
//
// 🔴 **周期（24 時間）はここに置かない。** 周期は計画 ADR-0035 決定 3 が「日次」と定めた値であり、
// 構成で変えられる形にしない（`ClusterDetectionHostedService.Interval`）。ここに在るのは「いつ判定するか」の待ちだけ。
//
// 🔴 **`ValidateOnStart` を付けない。** 不正値で起動を落とすと本サービスの `DocumentUpdated` /
// `DocumentDeleted` 購読ごと止まる。不正値は既定へ倒す（`ClusterSummaryOptions` と同じ向き）。
public sealed class ClusterDetectionOptions
{
    public const string SectionName = "ClusterDetection";

    // 起動から最初の判定までの待ち。**既定 2 分。** 起動直後はマイグレーション・seed・購読の立ち上がりと重なる。
    // 0 は「待たない」として受ける。負は既定へ倒す。
    public static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromMinutes(2);

    // リースが取れない・周期が失敗した後、判定し直すまでの待ち。**既定 1 時間。**
    // 失敗は成功として記録しないので、次の判定は「まだ期限切れ」を見て走り直す。
    // 短すぎると、壊れ続ける周期が 2 万件の文書を何度も読み直す。0 以下は既定へ倒す。
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromHours(1);

    public TimeSpan StartupDelay { get; set; } = DefaultStartupDelay;

    public TimeSpan RetryDelay { get; set; } = DefaultRetryDelay;

    public TimeSpan EffectiveStartupDelay => StartupDelay >= TimeSpan.Zero ? StartupDelay : DefaultStartupDelay;

    public TimeSpan EffectiveRetryDelay => RetryDelay > TimeSpan.Zero ? RetryDelay : DefaultRetryDelay;

    public bool HasInvalidValue => StartupDelay < TimeSpan.Zero || RetryDelay <= TimeSpan.Zero;
}
