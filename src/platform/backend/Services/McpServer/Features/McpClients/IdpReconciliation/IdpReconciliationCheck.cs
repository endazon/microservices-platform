using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Infrastructure.ExternalServices;
using McpServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace McpServer.Features.McpClients.IdpReconciliation;

// FR-16, SC-12, 計画 ADR-0123 決定 2・フォローアップ 2, ADR-0006, [[IADR-0516]] 決定 5（2026-10-09 追記 / #1818）:
// **登録簿と IdP のサービスアカウントの属性の食い違いを、定期の照合で検知して知らせる**（定期の照合 ＋ 計器 ＋ 警報。
// 形は [[IADR-0481]] の所有者の読み取りのポリシーの検査と同じ）。
//
// ■ 知らせ方は既存の運用の経路である（ADR-0006・[[IADR-0165]]）。本器は計器とログを出し、警報のルール
//   `McpClientIdpDrift` / `McpClientIdpReconciliationSeriesAbsent`（`deploy/prometheus/alerts.yml` と写し 3 か所）が鳴らす。
// ■ 🔴 **検知するだけで、IdP にも登録簿にも書かない**（自動の修復はしない。IADR-0516 決定 5）。読む口は書き込みを持たない
//   `IServiceAccountDirectory` であり、登録簿は追跡なしで読む。
// ■ 🔴 **opt-in にしない。** 書き込み口が構成されていなければ照合は失敗として数え（系列なし）、「系列が無い」の警報が鳴る。
// ■ 有人の行は比べない（IdP へ書かない既知の逸脱。IADR-0516 決定 3）。`enabled` も比べない（無効化の IdP への写し〔決定 4a〕は未実装で、
//   比べると無効化した行がすべて食い違いになる）。
public sealed record IdpReconciliationOptions(TimeSpan Interval)
{
    public const string IntervalKey = "McpClientProvisioning:Reconciliation:Interval";

    // 既定の周期。食い違いが起きてから警報が鳴るまでの遅れは「周期 ＋ 1 回の照合（期限は周期と同じ）＋ 警報の for（5 分）」。
    // 1 回の照合は IdP へ「クライアントの列挙（100 件ごとに 1 要求）＋ 無人の行ごとに 1 要求」を送る（並行は最大 4）。
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    /// <summary>行ごとの IdP の読み取りの並行の上限（IdP へ同時に送る要求の数を有界に保つ）。</summary>
    public const int MaxConcurrency = 4;

    /// <summary>
    /// 1 回の照合の期限。周期と同じ長さにする（次の周期に重ねない）。超えたら失敗として数える。
    /// 各要求の期限は口の HttpClient の Timeout（`McpClientProvisioning:Keycloak:TimeoutSeconds`。既定 10 秒）が持つ。
    /// </summary>
    public TimeSpan CycleTimeout => Interval;

    // 書式・範囲は `OwnerReadPolicyCheck:Interval`（IADR-0481）と同じ規則（`hh:mm:ss`・1 分以上 23:59:59 以下）。
    // 値域外は起動時に落とす —— 打ち間違いを既定へ黙って倒すと、設定したつもりの周期で回らない。
    public static IdpReconciliationOptions FromConfiguration(IConfiguration configuration)
    {
        var declared = configuration[IntervalKey];
        if (string.IsNullOrWhiteSpace(declared))
            return new IdpReconciliationOptions(DefaultInterval);
        if (TimeSpan.TryParseExact(declared.Trim(), @"hh\:mm\:ss",
                System.Globalization.CultureInfo.InvariantCulture, out var t)
            && t >= MinimumInterval)
            return new IdpReconciliationOptions(t);
        throw new InvalidOperationException(
            $"{IntervalKey} の値 '{declared}' は不正である（hh:mm:ss 形式・00:01:00〜23:59:59。例 00:01:00）。");
    }
}

/// <summary>食い違いの種類（issue #1818 の受け入れ基準 2）。</summary>
public enum IdpDriftKind
{
    /// <summary>登録簿に無人の行があるのに、IdP に同じ clientId のクライアントが無い（直接の削除・入口ができる前の行）。</summary>
    ClientMissing,

    /// <summary>IdP にクライアントはあるが、入口の印（managed-by）が無い（入口を通らずに作られた主体と同名の行）。</summary>
    NotManaged,

    /// <summary>入口の印つきのクライアントはあるが、認可サービスと同じ照会でサービスアカウントの利用者が引けない。</summary>
    ServiceAccountMissing,

    /// <summary>サービスアカウントの属性が登録簿の行と違う（IdP での直接の割当・交差した差し替えの後勝ち）。</summary>
    AttributesDiffer,

    /// <summary>入口の印つきのクライアントが IdP にあるのに、登録簿に無人の行が無い（補償が走らなかった残骸）。</summary>
    Orphan,
}

public sealed record IdpDrift(string ClientId, IdpDriftKind Kind)
{
    /// <summary>ログに出す種類の名前（運用仕様書・警報の説明文と同じ綴り）。</summary>
    public string KindLabel => Kind switch
    {
        IdpDriftKind.ClientMissing => "client_missing",
        IdpDriftKind.NotManaged => "not_managed",
        IdpDriftKind.ServiceAccountMissing => "service_account_missing",
        IdpDriftKind.AttributesDiffer => "attributes_differ",
        IdpDriftKind.Orphan => "orphan",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
    };
}

// 計器。🔴 **ゲージは「直近の照合で数えた食い違いの件数」だけを出す。** 照合に失敗したとき・まだ 1 度も照合していないときは
// 系列を出さない —— 古い値を出すと新しい食い違いを隠し、0 を出すと IdP の障害を「食い違いは無い」と偽る（IADR-0481 と同じ理由）。
// 系列が無い状態は `McpClientIdpReconciliationSeriesAbsent` が「見ていない」として知らせる。
// 🔴 **クライアント ID を属性に載せない**（系列の数を有界に保つ）。どの行かはログが名指しする。
public sealed class IdpReconciliationMetrics
{
    // McpServer のサービス名の Meter（Program.cs の AddMeter で収集する）。
    public const string MeterName = "microservices-platform.mcp-server";

    // Prometheus 側の名前は `mcp_idp_reconciliation_drifted`（`.` は `_`。単位 `{client}` は接尾辞にならない。
    // ゲージに `_total` は付かない）。scripts.repo.test.js が警報の式と突き合わせる。
    public const string DriftedGaugeName = "mcp.idp_reconciliation.drifted";
    public const string CheckCounterName = "mcp.idp_reconciliation.checks.total";
    public const string OutcomeTag = "mcp.idp_reconciliation.outcome";

    public const string OutcomeMatch = "match";
    public const string OutcomeDrift = "drift";
    public const string OutcomeFailed = "failed";

    private readonly Counter<long> _checks;
    private readonly Lock _gate = new();
    private int? _lastDrifted;

    public IdpReconciliationMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge(
            DriftedGaugeName, Observe, unit: "{client}",
            description: "直近の照合で、SC-12 の登録簿（無人の行）と IdP のサービスアカウントが食い違ったクライアントの件数"
                       + "（IdP に無い・入口の印が無い・サービスアカウントが無い・属性が違う・登録簿に無い印つきのクライアント）。"
                       + "0 が正常。直近の照合に失敗したとき・未照合のときは系列を出さない。");
        _checks = meter.CreateCounter<long>(
            CheckCounterName, unit: "{check}",
            description: "登録簿と IdP の照合の結末。outcome = match / drift / failed（1 回の照合に 1 つ）。");
    }

    /// <summary>直近の照合で数えた食い違いの件数（未照合・失敗は null）。</summary>
    public int? LastDrifted
    {
        get { lock (_gate) return _lastDrifted; }
    }

    public void RecordDrifted(int count)
    {
        lock (_gate) _lastDrifted = count;
        _checks.Add(1, new TagList { { OutcomeTag, count > 0 ? OutcomeDrift : OutcomeMatch } });
    }

    public void RecordFailure()
    {
        lock (_gate) _lastDrifted = null;
        _checks.Add(1, new TagList { { OutcomeTag, OutcomeFailed } });
    }

    private IEnumerable<Measurement<int>> Observe()
    {
        var drifted = LastDrifted;
        if (drifted is { } d) yield return new Measurement<int>(d);
    }
}

// 1 回の照合。
public sealed class IdpReconciliationCheck(
    McpDbContext db,
    IServiceAccountDirectory directory,
    IdpReconciliationMetrics metrics,
    IdpReconciliationOptions options,
    TimeProvider clock,
    ILogger<IdpReconciliationCheck> logger)
{
    /// <summary>1 回の照合で行ごとに名指しする上限（超えた分は件数だけ。ログの量を有界に保つ）。</summary>
    internal const int MaxNamedDrifts = 20;

    internal sealed record RegistryRow(string ClientId, IReadOnlyDictionary<string, string> Attributes);

    /// <summary>
    /// 比べる（純粋な読み取り）。IdP の一覧を 1 度読み、入口の印つきのクライアントの行だけサービスアカウントの属性を読む
    /// （並行は <see cref="IdpReconciliationOptions.MaxConcurrency"/> まで）。読めなければ例外（途中までの結果で数えない）。
    /// </summary>
    internal static async Task<IReadOnlyList<IdpDrift>> FindDriftsAsync(
        IReadOnlyList<RegistryRow> rows, IServiceAccountDirectory directory, CancellationToken ct)
    {
        var clients = await directory.ListClientsAsync(ct);
        var managedById = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var c in clients) managedById[c.ClientId] = c.Managed;

        var drifts = new ConcurrentBag<IdpDrift>();
        var toRead = new List<RegistryRow>();
        foreach (var row in rows)
        {
            if (!managedById.TryGetValue(row.ClientId, out var managed))
                drifts.Add(new IdpDrift(row.ClientId, IdpDriftKind.ClientMissing));
            else if (!managed)
                drifts.Add(new IdpDrift(row.ClientId, IdpDriftKind.NotManaged));
            else
                toRead.Add(row);
        }

        await Parallel.ForEachAsync(
            toRead,
            new ParallelOptions { MaxDegreeOfParallelism = IdpReconciliationOptions.MaxConcurrency, CancellationToken = ct },
            async (row, token) =>
            {
                var actual = await directory.ReadServiceAccountAttributesAsync(row.ClientId, token);
                if (actual is null)
                    drifts.Add(new IdpDrift(row.ClientId, IdpDriftKind.ServiceAccountMissing));
                // 書き込み口の読み戻しと同じ比べ方（集合値は集合として。IADR-0385）。比べ方を 2 つにしない。
                else if (!KeycloakServiceAccountProvisioner.SameAttributes(row.Attributes, actual))
                    drifts.Add(new IdpDrift(row.ClientId, IdpDriftKind.AttributesDiffer));
            });

        var registered = new HashSet<string>(rows.Select(r => r.ClientId), StringComparer.Ordinal);
        foreach (var c in clients)
        {
            if (c.Managed && !registered.Contains(c.ClientId))
                drifts.Add(new IdpDrift(c.ClientId, IdpDriftKind.Orphan));
        }

        return [.. drifts.OrderBy(d => d.ClientId, StringComparer.Ordinal).ThenBy(d => d.Kind)];
    }

    /// <summary>
    /// 照合して計器とログへ出す。照合できなければ null（ゲージの系列を止める）。停止要求の取り消しはそのまま外へ出す。
    /// 1 回の照合の期限（<see cref="IdpReconciliationOptions.CycleTimeout"/>）を超えたら失敗として数える。
    /// </summary>
    public async Task<IReadOnlyList<IdpDrift>?> RunAsync(CancellationToken stoppingToken)
    {
        using var deadline = new CancellationTokenSource(options.CycleTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
        int rowCount;
        IReadOnlyList<IdpDrift> drifts;
        try
        {
            // 登録簿は追跡なしで読む（照合は書かない）。
            var rows = (await db.Clients.AsNoTracking()
                    .Where(c => c.Kind == McpClientKind.ServiceAccount)
                    .ToListAsync(linked.Token))
                .Select(c => new RegistryRow(c.ClientId, c.Attributes))
                .ToList();
            rowCount = rows.Count;
            drifts = await FindDriftsAsync(rows, directory, linked.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IdpProvisioningException ex) when (ex.Failure == IdpProvisioningFailure.Unavailable)
        {
            metrics.RecordFailure();
            logger.LogWarning(
                "IdP の照合の口が構成されていない（McpClientProvisioning:Provider）。登録簿と IdP を照合できないので計器の系列を止める"
                + "（警報 McpClientIdpReconciliationSeriesAbsent）。");
            return null;
        }
        catch (Exception ex)
        {
            metrics.RecordFailure();
            logger.LogError(ex,
                "登録簿と IdP を照合できなかった（IdP・登録簿を読めない、または 1 回の照合の期限 {Timeout} を超えた）。"
                + "食い違いの有無が分からないので計器の系列を止める（警報 McpClientIdpReconciliationSeriesAbsent）。次の周期で再試行する。",
                options.CycleTimeout);
            return null;
        }

        var previous = metrics.LastDrifted;
        metrics.RecordDrifted(drifts.Count);
        foreach (var drift in drifts.Take(MaxNamedDrifts))
        {
            logger.LogWarning(
                "登録簿と IdP の食い違いを検知した: client={ClientId} kind={Kind}。照合は直さない"
                + "（運用仕様書「MCP クライアント登録簿と認証基盤の照合」の手順で確かめる）。",
                ForLog(drift.ClientId), drift.KindLabel);
        }
        if (drifts.Count > MaxNamedDrifts)
            logger.LogWarning("ほかに {Count} 件の食い違いがある（名指しは 1 回に {Max} 件まで）。",
                drifts.Count - MaxNamedDrifts, MaxNamedDrifts);
        logger.LogInformation(
            "登録簿と IdP を照合した: 無人の行 {Rows} 件・食い違い {Drifted} 件。", rowCount, drifts.Count);
        if (drifts.Count == 0 && previous is > 0)
            logger.LogInformation("登録簿と IdP の食い違いが無くなった。");
        return drifts;
    }

    // CodeQL（Log entries created from user input）: 登録簿・IdP の clientId は利用者の入力に由来する。制御文字を潰し、長さを切る。
    private static string ForLog(string value)
    {
        var cleaned = new string(Array.ConvertAll(value.ToCharArray(), c => char.IsControl(c) ? '_' : c));
        return cleaned.Length <= 128 ? cleaned : cleaned[..128] + "…";
    }
}

// 常駐。起動時に 1 回、以後は周期ごとに照合する。1 回の失敗で常駐を止めない。周期は TimeProvider で数える。
public sealed class IdpReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    IdpReconciliationOptions options,
    IdpReconciliationMetrics metrics,
    TimeProvider clock,
    ILogger<IdpReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval, clock);
        try
        {
            do
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IdpReconciliationCheck>().RunAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // RunAsync が照合できないことを自分で扱うので、ここへ来るのはスコープの解決などの想定外だけである。
                    // それでも前の周期の値を出し続けない（系列を止めて「見ていない」の警報へ倒す）。
                    metrics.RecordFailure();
                    logger.LogError(ex, "登録簿と IdP の照合で想定外の例外が発生した。次の周期で再試行する。");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 周期の待ちの間の停止。静かに終わる（停止を常駐の失敗として外へ出さない）。
        }
    }
}
