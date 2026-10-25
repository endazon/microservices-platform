namespace NotificationService.Features.Notifications.Accept;

// FR-22, IADR-0215 決定 2・3, IADR-0270 決定 6: 受け口の要求（gRPC の `AcceptRequest` を写した形。［2026-10-15 / #1902］REST の受け口は撤去した）。
//
// ★ **送信側（DocumentService の 旧 REST の `HttpPrivateNoteNotifier`（［2026-10-10］撤去））が送る形と 1 バイトずれない。**
// platform → knowledge の参照は禁止のため型を共有できない。**同じ形を複製し、一致はテストで固定する**
// （通知種別の定数で採ったのと同じ扱い）。
//
// ★ **自由文の項目は 1 つも無い。** 計画 FR-22「本文が件数と期限のみで構成される。資料のタイトル・
// 本文・検索語・回答内容を含まない」を、受け口の側でも**型の形**で守る。ここに自由文の口を開けると、
// 送信側がどれだけ規律を守っても、いつか別の呼び出し元がそこへ資料のタイトルを入れる。
//
// ★ **すべて null 許容にしてある。** 必須項目を非 null で宣言すると、欠落が既定値（空文字・
// `0001-01-01`）として黙って通り、**入口の 400 ではなく壊れた通知の永続化**になる。
// 欠落を欠落として受け取り、`NotificationIngress` が 400 へ倒す。
public sealed record NotificationIngressRequest(
    string? Subject,
    string? Kind,
    DateTimeOffset? OccurredAt,
    int? Count,
    int? ThresholdPercent,
    DateTimeOffset? Deadline);

// ［2026-10-15 / #1902］REST の受け口の応答（`NotificationIngressResultDto`）は受け口とともに撤去した。
// 畳んだ事実は gRPC の `AcceptResponse.duplicate` が運ぶ。
