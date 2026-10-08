namespace LlmGateway.Common.Observability;

// FR-11, NFR-19, IADR-0104 追記・IADR-0504 追記 (#1819): 同じ事象のログを「初回は即時・以後は一定間隔で要約 1 行」に抑える。
//
// 🔴 **なぜ要るか。** #1819 の PoC では、鍵未設定の埋め込み失敗がスタック付きで 1,146 回記録され、
// ゲートウェイのコンテナのログ（約 1MB）から他の行（取引判断の呼び出し）を押し出した。
// 同じ原因の同じ行を何百回も残しても情報は増えない。**残すべきは「起きた」「何回起きた」の 2 つ**である。
//
// 規則（鍵ごと）:
//   - 初めての鍵 → 記録する（抑えた件数 0）。
//   - 前回記録から SummaryInterval 未満 → 抑える（件数だけ数える）。
//   - SummaryInterval 以上経った次の発生 → 記録する（前回記録以降に抑えた件数を添える）。
// 要約は**次の発生時に**出す（タイマーを持たない）。発生が止まった後の残りの件数は出ない —— その間は
// 何も起きていないので、押し出す行も無い。
//
// 🔴 **鍵は値域の閉じた値だけで作る**（呼び出し側の責務）。利用者由来の自由文字列を鍵にすると
// 辞書が非有界に育つ（IADR-0110 がメトリクスの属性で閉じたのと同じ理由）。
public sealed class LogOccurrenceThrottle(TimeProvider time)
{
    // 5 分: PoC の取り込み（再試行 2s / 10s / 30s）と取引判断の周期のどちらでも、1 時間あたり高々 12 行に収まる。
    public static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry(DateTimeOffset lastLogged)
    {
        public DateTimeOffset LastLogged { get; set; } = lastLogged;
        public long Suppressed { get; set; }
    }

    /// <summary>
    /// 今回の発生を記録すべきなら <c>true</c> を返し、<paramref name="suppressedSinceLast"/> に
    /// 前回記録以降に抑えた件数を入れる（初回は 0）。抑えるなら <c>false</c>。
    /// </summary>
    public bool ShouldLog(string key, out long suppressedSinceLast)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                _entries[key] = new Entry(now);
                suppressedSinceLast = 0;
                return true;
            }

            if (now - entry.LastLogged >= SummaryInterval)
            {
                suppressedSinceLast = entry.Suppressed;
                entry.LastLogged = now;
                entry.Suppressed = 0;
                return true;
            }

            entry.Suppressed++;
            suppressedSinceLast = 0;
            return false;
        }
    }
}
