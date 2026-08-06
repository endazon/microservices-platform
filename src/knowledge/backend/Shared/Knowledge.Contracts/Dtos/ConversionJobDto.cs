namespace Knowledge.Contracts.Dtos;

// FR-12, UC-06, SC-07: 変換ジョブの状況 DTO（BFF ↔ SPA 契約）。ConversionService が保持する変換
// 読み取りモデルを表す。Status は queued / processing / succeeded / failed。失敗ジョブは Error を持つ。
//
// SC-07（05_screens:324・裁定 Q13）: デッドレター標識（DeadLettered）と試行上限（MaxAttempts）を持つ。
// **標識は Status の 5 値目ではない**——同 :308 が「ジョブ状態モデルは 4 値である…デッドレターの表示は
// failed の内訳として扱う」と定めるため、独立した真偽値にしてある（IADR-0136 決定 1）。
// 新メンバーは**末尾に既定値つきで**足す（既定値の無い追加・位置の入れ替えは契約上の破壊的変更。
// IADR-0122 決定 2）。
public record ConversionJobDto(
    Guid Id,
    Guid SourceId,
    string SourceType,
    string OriginalPath,
    string Status,
    string? Error,
    Guid? DocumentId,
    string? MarkdownUri,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    // 再試行を使い切って <queue>_error（デッドレター）へ送られたか。true の間は自動では直らない
    // （人が原本に手を入れるまで再実行しても同じ結果になる）ことを示す。Status == failed のときだけ真。
    bool DeadLettered = false,
    // 1 回の配信で行う自動再試行の試行上限（初回 ＋ 再試行）。**手動再変換の回数上限ではない**
    // （05_screens:310「手動再変換の回数上限は設けない」）。
    int MaxAttempts = ConversionJobRetryPolicy.MaxAttempts);

// FR-12, UC-06, SC-07: 変換ジョブの状態値。
public static class ConversionJobStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

// FR-12, SC-07: 自動再試行の試行上限（初回 1 回 ＋ 再試行 3 回）。
// **実体は Platform.Shared.Infrastructure の UsePlatformRetry（再試行間隔 3 段）である。**
// 契約プロジェクトから基盤プロジェクト（MassTransit 依存）を参照しないため値をここに置き、
// 両者が一致することは ConversionJobTests.MaxAttempts_matches_platform_retry_policy が束ねる
// （IADR-0136 決定 3・決定 4）。record の既定値に使うため const である。
public static class ConversionJobRetryPolicy
{
    public const int MaxAttempts = 4;
}
