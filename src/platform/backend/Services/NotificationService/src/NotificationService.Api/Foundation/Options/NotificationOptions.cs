namespace NotificationService.Api.Foundation.Options;

// FR-22, ADR-0045 決定 3, IADR-0215 決定 2・4: NotificationService の設定値。
//
// **上限を定数で埋め込まない。** ADR-0045 決定 1 が想定するテナントは 3 種（Workspace 2,000 /
// SMTP relay 10,000 / 個人アカウント 500）あり、go-live の実値は未確定である
// （IADR-0215 フォローアップ 2）。既定は**最も厳しい 500** を採る —— 緩い値を既定にすると、
// 個人アカウント運用（ADR-0045 が例外として認めた形）で**初日から静かに落ちる**。
public sealed class NotificationOptions
{
    public const string SectionName = "Notification";

    // ADR-0045 決定 1-b: 個人アカウントの 1 日あたり送信上限。**最も厳しい値が既定である。**
    public int DailyEmailLimit { get; set; } = 500;

    // IADR-0215 決定 2: アプリ内通知の保持期間。個人資料の論理削除の保管期間（90 日・ADR-0037
    // 決定 5）へ揃えた実装側の判断であり、計画に根拠は無い。
    public int RetentionDays { get; set; } = 90;

    // 一覧の既定件数と上限（通信仕様書。BFF もクランプするが、後段でも守る）。
    public int DefaultListLimit { get; set; } = 50;
    public int MaxListLimit { get; set; } = 100;

    // 保持期限切れの削除と outbox の送出を回す常駐処理。**テストでは無効にする**
    // （器がホストを起こすたびに背景処理が走ると、時刻に依存した検証が不安定になる）。
    public bool MaintenanceEnabled { get; set; } = true;
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(5);
}
