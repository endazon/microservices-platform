namespace Knowledge.Contracts.Dtos;

// FR-12, UC-06, SC-07: 変換ジョブの状況 DTO（BFF ↔ SPA 契約）。ConversionService が保持する変換
// 読み取りモデルを表す。Status は queued / processing / succeeded / failed。失敗ジョブは Error を持つ。
//
// SC-07（05_screens:324・裁定 Q13）: デッドレター標識（DeadLettered）と試行上限（MaxAttempts）を持つ。
// **標識は Status の 5 値目ではない**——同 :308 が「ジョブ状態モデルは 4 値である…デッドレターの表示は
// failed の内訳として扱う」と定めるため、独立した真偽値にしてある（IADR-0137 決定 1）。
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
    // 自動再試行を使い切った（＝デッドレター）か。true の間は自動では直らない
    // （人が原本に手を入れるまで再実行しても同じ結果になる）ことを示す。Status == failed のときだけ真。
    bool DeadLettered = false,
    // 1 回の配信で行う自動再試行の試行上限（初回 ＋ 再試行）。**手動再変換の回数上限ではない**
    // （05_screens:310「手動再変換の回数上限は設けない」）。
    int MaxAttempts = ConversionJobRetryPolicy.MaxAttempts,
    // SC-07（hi-fi:420-422）: 図のコード化の内訳。**状態の 5 値目ではない**——「✕ 図コード化失敗
    // （画像保持へ縮退済み）」は DiagramsRetained > 0 から導出する表示であり、ジョブ自体は succeeded
    // である（05_screens:320「ジョブ状態モデルは 4 値である」。DeadLettered と同じ扱い。IADR-0154 決定 5）。
    // IADR-0127「状態表示は契約から導出できる値だけで作る」に従い、導出元を DTO へ載せる。
    int DiagramsCoded = 0,
    int DiagramsRetained = 0,
    // SC-07（hi-fi:422）「補正あり」の標識。**再変換すると失われる補正があること**を示す
    // （05_screens:313・333。IADR-0154 決定 4）。
    bool HasCorrection = false,
    // SC-07, ADR-0070 決定 3 / IADR-0356 (#1192): **「本文なしで完了」の標識。** テキスト層を持たない PDF
    // （スキャン等）は本文が存在しないため、`failed` にせず `succeeded` の内訳として理由つきで表示する。
    // **状態の 5 値目ではない**（DeadLettered / DiagramsRetained と同じ扱い）。再試行もデッドレターもしない
    // （何度やっても結果は変わらない）。Status == succeeded のときだけ真。
    bool BodyAbsent = false);

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
// 両者が一致することは
// RawDocumentFetchedConsumerJobTests.MaxAttempts_contract_constant_matches_platform_retry_policy が束ねる
// （IADR-0137 決定 3・決定 4）。record の既定値に使うため const である。
public static class ConversionJobRetryPolicy
{
    public const int MaxAttempts = 4;
}
