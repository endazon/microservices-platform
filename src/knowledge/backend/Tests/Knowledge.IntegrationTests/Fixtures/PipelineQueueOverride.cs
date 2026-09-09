using System.Text.Json;
using System.Text.Json.Nodes;

namespace Knowledge.IntegrationTests.Fixtures;

// FR-14, NFR, ADR-0018, ADR-0027 手順 3, #1337:
// **正本 pipeline.json から「段の `queue` だけを差し替えた宣言」を実行時に派生させる器。**
//
// ■ なぜ要るのか（#1337 の実測に基づく）
//   fan-out の統合テスト 2 クラスは、購読キュー名を**本番の固定サービス名 ＋ 固定の第 2 要素**で
//   取っていた（`ingestion-service.DocumentUpdated` / `wiki-service.DocumentUpdated`）。
//   Testcontainers 経路ではクラスごとにブローカが新品なので問題にならないが、
//   **外部から与えたブローカ（`PLATFORM_TEST_RABBITMQ`）では全クラスが 1 台を共有する**。
//
//   🔴 そのキューは本番の Program 配線（`BindPlatformQueue<DocumentUpdated>`）によって
//   `DocumentUpdated` exchange へ**恒久的に**束縛され、**ホストを破棄しても束縛は残る**。
//   同じ run の他クラス（`DocumentCrudTests` 等）が本物の `DocumentUpdated` を発行し続けるため、
//   **消費者が居ない間、本物のメッセージがそのキューへ溜まる**。fan-out テストが起きると、
//   自分の 1 通に辿り着く前に滞留分を先に処理することになり、30 秒の予算を使い切って落ちる。
//
//   他のブローカ試験（`WolverineBrokerEdge` / `RawDocumentFetchedEdge`）は
//   **サービス名を runId でスコープして**この罠を最初から避けている。fan-out の 2 クラスだけが
//   本番の固定サービス名を必要とする（本番の Program 配線をそのまま起こすため）ので、
//   **スコープできるのはキュー名の第 2 要素、すなわち宣言（pipeline.json）の `queue` である。**
//
// ■ 🔴 主張を弱めない
//   `queue` を差し替えても、キュー名を作るのは**手順 3 の適用点**（`ListenToPlatformQueue` →
//   `PlatformQueueName`）のままである。**前置がサービスごとにキューを分ける**という
//   検査対象の性質は 1 ミリも変わらない —— 変わるのは第 2 要素が実行ごとに一意になることだけである。
//   `AutoPurgeOnStartup()` は使わない（器から本番の Program 配線を書き換えると、
//   **本番と同じ配線で fan-out が成り立つ**という主張そのものが試験されなくなる）。
//
// ■ 🔴 手で書き写さない
//   `QueueOverrideFanOutTests` が U0d で確立した原則をそのまま引き継ぐ ——
//   規則 2（宣言があるのに段が未宣言なら起動失敗）が**登録される全段の宣言**を要求するため、
//   派生元は正本でなければならない。書き写せば本番の宣言が変わったときに黙って腐る。
internal static class PipelineQueueOverride
{
    /// <summary>正本 pipeline.json のリポジトリ根からの相対パス。</summary>
    internal static string SourceRelativePath { get; } =
        Path.Combine("deploy", "helm", "microservices-platform", "files", "pipeline.json");

    /// <summary>
    /// 正本の宣言（JSON 文字列）から、<paramref name="queueByStep"/> が指す段の <c>queue</c> だけを
    /// 差し替えた宣言を返す。**純関数**（ファイルに触らない）であり、単体試験はこちらを直接呼ぶ。
    /// </summary>
    /// <param name="sourceJson">正本 pipeline.json の内容。</param>
    /// <param name="queueByStep">段名 → 宣言する <c>queue</c>。</param>
    /// <param name="origin">派生元の説明（例外メッセージへ載せる）。</param>
    /// <exception cref="InvalidOperationException">
    /// 🔴 **fail-closed**: 差し替えが 1 件でも当たらなかったとき・同じ段名が複数あるとき・
    /// 前置後のキュー名が同一サービス内で衝突するときは、派生せずに止める。
    /// 黙って通すと「宣言したつもりで既定キューを聴いている」状態が緑と見分けられなくなる
    /// （本器が塞ごうとしている滞留の罠が、そのまま残ったまま緑になる）。
    /// </exception>
    internal static string Derive(
        string sourceJson, IReadOnlyDictionary<string, string> queueByStep, string? origin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceJson);
        ArgumentNullException.ThrowIfNull(queueByStep);
        if (queueByStep.Count == 0)
        {
            throw new ArgumentException("差し替える段が 1 つも指定されていない。", nameof(queueByStep));
        }
        foreach (var (step, queue) in queueByStep)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(step, nameof(queueByStep));
            ArgumentException.ThrowIfNullOrWhiteSpace(queue, nameof(queueByStep));
        }

        var where = origin is null ? "" : $" 派生元: {origin}";
        var root = JsonNode.Parse(sourceJson)?.AsObject()
            ?? throw new InvalidOperationException($"pipeline.json を JSON オブジェクトとして読めなかった。{where}");
        var steps = root["steps"]?.AsArray()
            ?? throw new InvalidOperationException($"pipeline.json に steps 配列が無い。{where}");

        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        // 前置後のキュー名の衝突検査に使う「サービス → 実効キュー名 → 段名」。
        var effective = new List<(string Service, string Queue, string Step)>();

        foreach (var node in steps)
        {
            var step = node?.AsObject()
                ?? throw new InvalidOperationException($"steps に段オブジェクトでない要素がある。{where}");
            var name = step["name"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"name を持たない段がある。{where}");
            var service = step["service"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"段 {name} が service を持たない。{where}");

            if (queueByStep.TryGetValue(name, out var queue))
            {
                step["queue"] = queue;
                hits[name] = hits.GetValueOrDefault(name) + 1;
            }

            // 🔴 **実効キュー名は本番の式と同じに導く** —— `step?.Queue ?? nameof(TEvent)`
            //（`WikiService/Program.cs` ・`IngestionService/Program.cs`）。差し替えなかった段の
            // 既定キューと、差し替えた段の宣言値が**衝突し得る**ため、両方を同じ土俵で見る。
            var declared = step["queue"]?.GetValue<string>();
            var input = step["input"]?.GetValue<string>();
            var resolved = declared ?? input
                ?? throw new InvalidOperationException($"段 {name} が queue も input も持たない。{where}");
            effective.Add((service, resolved, name));
        }

        var missing = queueByStep.Keys.Where(n => !hits.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"pipeline.json の段 [{string.Join(", ", missing)}] に queue を入れられなかった。"
                + " 段名が変わった可能性がある（このまま進めると購読者は既定キューを聴き、"
                + "テストが落ちた理由を取り違える）。" + where);
        }

        var duplicated = hits.Where(h => h.Value > 1).Select(h => h.Key)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (duplicated.Length > 0)
        {
            throw new InvalidOperationException(
                $"pipeline.json に同名の段が複数ある [{string.Join(", ", duplicated)}]。"
                + " どちらへ差し替えたのかが決まらない。" + where);
        }

        // 🔴 同一サービスの 2 段が同じキュー名になると、**2 つの購読が 1 本のキューへ潰れる**
        // （`ListenToPlatformQueue` を同じ名前で 2 回呼ぶ形）。fan-out の退行と症状が似るため、
        // 器の側で作り込まないよう派生の時点で止める。
        var collisions = effective
            .GroupBy(e => (e.Service, e.Queue))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Service}.{g.Key.Queue} ← [{string.Join(", ", g.Select(x => x.Step).Order(StringComparer.Ordinal))}]")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (collisions.Length > 0)
        {
            throw new InvalidOperationException(
                "前置後のキュー名が同一サービス内で衝突する: " + string.Join(" / ", collisions)
                + "。2 つの購読が 1 本のキューへ潰れるため派生しない。" + where);
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// 正本 pipeline.json から派生させた宣言を一時ファイルへ書き出し、そのパスを返す。
    /// 呼び出し側は <c>UseSetting("Pipeline:ConfigPath", path)</c> でホストへ渡し、
    /// 破棄時にファイルを消す（消し忘れると次回以降が古い宣言を拾い得る）。
    /// </summary>
    internal static string WriteDerivedFixture(IReadOnlyDictionary<string, string> queueByStep, string filePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePrefix);

        var source = RepoFile.Find(SourceRelativePath,
            because: "派生元の正本が読めなければ、宣言を差し替えた fixture は作れない。"
                + " ここで止めないと、購読者が既定キュー（共有ブローカで滞留し得る側）を聴いたまま緑になる。");

        var derived = Derive(File.ReadAllText(source), queueByStep, source);
        var path = Path.Combine(Path.GetTempPath(), $"{filePrefix}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, derived);
        return path;
    }
}
