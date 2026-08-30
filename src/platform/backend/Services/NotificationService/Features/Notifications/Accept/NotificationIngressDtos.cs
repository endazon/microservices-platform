namespace NotificationService.Features.Notifications.Accept;

// FR-22, IADR-0215 決定 2・3, IADR-0270 決定 6: 受け口（POST /internal/notifications）の要求本文。
//
// ★ **送信側（DocumentService の HttpPrivateNoteNotifier）が送る形と 1 バイトずれない。**
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

// FR-22: 受け口の応答。**2 項目だけで、通知の中身を返さない。**
//
// `Duplicate` は「同一事象の再送を畳んだ」ことを表す（201 = 新規 / 200 = 畳んだ）。
// 送信側は成否しか見ないが、**畳んだ事実が観測できないと「届いていないのか、二重送信を
// 止めたのか」が後から区別できない**。
public sealed record NotificationIngressResultDto(Guid Id, bool Duplicate);
