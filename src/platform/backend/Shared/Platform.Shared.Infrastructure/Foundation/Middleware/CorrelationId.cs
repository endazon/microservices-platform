using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Platform.Shared.Infrastructure.Foundation.Middleware;

// NFR-19, ADR-0006, NFR-28, [[IADR-0649]] (#2129): 相関 ID の決め方と置き場所。
//
// ■ 受けた値（X-Correlation-ID）が安全な形ならそれを使う。無い・形が悪いなら**現在のトレース ID** にする。
//   後段は traceparent から同じトレース ID を導けるので、ヘッダが届かない経路（メッセージ・gRPC の再試行など）でも
//   相関 ID がトレース ID と一致する（相関はトレース ID に統一する）。
// ■ 🔴 受けた値は外から来る。ログのスコープと後段への転送に載るので、英数字と `._:-` の 128 文字までに限る（NFR-28）。
public static partial class CorrelationId
{
    public const string Header = "X-Correlation-ID";

    /// <summary>受けた（または決めた）相関 ID を要求の間だけ置く <c>HttpContext.Items</c> の鍵。</summary>
    public const string ItemKey = "Platform.CorrelationId";

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$")]
    private static partial Regex SafeForm();

    /// <summary>受けた値が安全な形ならそれを、そうでなければ現在のトレース ID（無ければ新しい GUID）を返す。</summary>
    public static string Resolve(string? received)
    {
        if (received is not null && SafeForm().IsMatch(received))
            return received;
        return Activity.Current is { IdFormat: ActivityIdFormat.W3C } current
            ? current.TraceId.ToHexString()
            : Guid.NewGuid().ToString("N");
    }
}
