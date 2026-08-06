using MassTransit;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

// ADR-0003: 非同期メッセージング（MassTransit + RabbitMQ）の「再試行・デッドレターで回復性を確保する」
// 決定を全サービス共通で満たすためのバス設定拡張。各 Program.cs の UsingRabbitMq 内で呼び出す。
public static class MassTransitExtensions
{
    // ADR-0003: 自動再試行の間隔。**要素数がそのまま再試行回数**である。
    private static readonly TimeSpan[] RetryIntervals =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    // FR-12, SC-07: 1 回の配信で行う試行の上限（初回 1 回 ＋ 自動再試行 RetryIntervals.Length 回）。
    // これに達した失敗は <queue>_error（デッドレター）へ送られる。**試行上限の単一情報源**であり、
    // 契約側の ConversionJobRetryPolicy.MaxAttempts が同値であることは単体テストで束ねている
    // （数字を 2 か所に書かないため。IADR-0137 決定 3）。
    public static int MaxAttempts => RetryIntervals.Length + 1;

    // ADR-0003: 一時的失敗（保存失敗・外部サービス呼び出しの一時エラー等）は間隔を空けて再試行する。
    // 再試行を使い切った継続失敗は MassTransit が自動で <queue>_error（デッドレター）へ送るため、
    // メッセージ喪失なく回復性を確保できる。ブローカ非依存の IBusFactoryConfigurator に対して適用する。
    public static void UsePlatformRetry(this IBusFactoryConfigurator cfg)
    {
        cfg.UseMessageRetry(r => r.Intervals(RetryIntervals));
    }
}
