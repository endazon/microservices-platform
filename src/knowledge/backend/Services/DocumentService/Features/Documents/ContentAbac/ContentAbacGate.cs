using System.Diagnostics.Metrics;
using DocumentService.Domain.Ports;

namespace DocumentService.Features.Documents.ContentAbac;

// FR-05, FR-19, NFR-09, 計画 ADR-0121 決定 2・4, ADR-0119 決定 3・4, [[IADR-0481]] (#1665):
// **内容の ABAC（組織文書の機密・部門・ライフサイクル）を有効にしてよいかの門。**
//
// ■ 開くのは「構成で有効（`ContentAbac:Mode=On`）」かつ「所有者の読み取りのポリシーが有効な状態で 1 件以上あると
//   認可サービスで確かめた」ときだけである（ADR-0121 決定 2「無ければ有効にしない」）。数えられないときも開かない（fail-closed）。
//   閉じている理由はログ（Warning）と計器（`documents.content_abac.gate.open` の属性 state）に残す。
// ■ 🔴 **1 度開いたら、プロセスの寿命の間は閉じない（ラッチ）。** 開いた後にポリシーが消えたことは、認可サービスの検査と
//   警報（`OwnerReadPolicyMissing`）が知らせる（ADR-0121 決定 2・§結果）。門を動的に閉じると、消えた瞬間に内容の ABAC が外れ、
//   機械クライアントの許可が**広がる**向きに倒れる。再起動したときは改めて確かめる。
// ■ 🔴 **本件の時点で門を読む判定は無い。** 内容の ABAC の本体（#1615）が `DocumentReadAccess` から `IsOpen` を読む。
public enum ContentAbacMode
{
    Off,
    On,
}

public sealed record ContentAbacOptions(ContentAbacMode Mode)
{
    public const string ModeKey = "ContentAbac:Mode";

    // 値域外の宣言は起動時に落とす（打ち間違いを Off へ黙って倒すと、有効にしたつもりで何も起きない。
    // On へ倒すと、確かめないまま有効になる向きの誤りを招く）。
    public static ContentAbacOptions FromConfiguration(IConfiguration configuration)
    {
        var declared = configuration[ModeKey];
        var mode = declared?.Trim().ToLowerInvariant() switch
        {
            null or "" or "off" => ContentAbacMode.Off,
            "on" => ContentAbacMode.On,
            _ => throw new InvalidOperationException(
                $"{ModeKey} の値 '{declared}' は不正である（Off / On のいずれか）。未設定なら Off。"),
        };
        return new ContentAbacOptions(mode);
    }
}

public enum ContentAbacGateState
{
    /// <summary>構成が Off。</summary>
    Disabled,

    /// <summary>構成は On だが、まだ 1 度も確かめていない。</summary>
    NotEvaluated,

    /// <summary>構成は On だが、所有者の読み取りのポリシーが有効な状態で 1 件も無い。</summary>
    OwnerReadPolicyAbsent,

    /// <summary>構成は On だが、所有者の読み取りのポリシーを数えられない（認可サービスに届かない等）。</summary>
    OwnerReadPolicyUnknown,

    /// <summary>開いている（内容の ABAC を有効にしてよい）。</summary>
    Open,
}

public interface IContentAbacGate
{
    bool IsOpen { get; }
    ContentAbacGateState State { get; }
}

public sealed class ContentAbacGate : IContentAbacGate
{
    // Meter 名はサービス名（`UnitProjectMetrics` 等と同じ器。収集対象は増えない）。
    public const string MeterName = "microservices-platform.document-service";

    // Prometheus 側の名前は `documents_content_abac_gate_open`（1 = 開・0 = 閉）。
    public const string OpenGaugeName = "documents.content_abac.gate.open";
    public const string StateTag = "documents.content_abac.gate.state";

    private readonly ContentAbacOptions _options;
    private readonly ILogger<ContentAbacGate> _logger;
    private readonly object _sync = new();
    private ContentAbacGateState _state;

    public ContentAbacGate(ContentAbacOptions options, IMeterFactory meterFactory, ILogger<ContentAbacGate> logger)
    {
        _options = options;
        _logger = logger;
        _state = options.Mode == ContentAbacMode.Off ? ContentAbacGateState.Disabled : ContentAbacGateState.NotEvaluated;
        meterFactory.Create(MeterName).CreateObservableGauge(
            OpenGaugeName, Observe, unit: "{gate}",
            description: "内容の ABAC の門。1 = 開（有効にしてよい）・0 = 閉。state = disabled / not_evaluated / "
                       + "owner_read_policy_absent / owner_read_policy_unknown / open。");
    }

    public ContentAbacGateState State
    {
        get { lock (_sync) return _state; }
    }

    public bool IsOpen => State == ContentAbacGateState.Open;

    public bool IsEnabledByConfiguration => _options.Mode == ContentAbacMode.On;

    /// <summary>
    /// 門を評価する。構成が Off なら認可サービスを呼ばない。1 度開いたら呼ばずに開いたままを返す。
    /// 呼び出し元の取り消しはそのまま外へ出す。
    /// </summary>
    public async Task<ContentAbacGateState> EvaluateAsync(IOwnerReadPolicyStatusSource source, CancellationToken ct)
    {
        if (_options.Mode == ContentAbacMode.Off) return ContentAbacGateState.Disabled;
        if (IsOpen) return ContentAbacGateState.Open;

        var count = await source.GetActiveCountAsync(ct);
        var next = count switch
        {
            null => ContentAbacGateState.OwnerReadPolicyUnknown,
            <= 0 => ContentAbacGateState.OwnerReadPolicyAbsent,
            _ => ContentAbacGateState.Open,
        };

        lock (_sync)
        {
            // ラッチ: 並行の評価が先に開けていたら閉じ直さない。
            if (_state == ContentAbacGateState.Open) return ContentAbacGateState.Open;
            _state = next;
        }

        switch (next)
        {
            case ContentAbacGateState.Open:
                _logger.LogInformation(
                    "内容の ABAC の門を開いた（所有者の読み取りのポリシーが有効な状態で {Count} 件ある）。以後この実行の間は閉じない。",
                    count);
                break;
            case ContentAbacGateState.OwnerReadPolicyAbsent:
                _logger.LogWarning(
                    "{Key}=On だが、所有者の読み取りのポリシーが有効な状態で 1 件も無いので、内容の ABAC を有効にしない（門は閉じたまま）。"
                    + "運用仕様書の「所有者の読み取りのポリシーの投入」の手順で投入すると、次の評価で開く。",
                    ContentAbacOptions.ModeKey);
                break;
            default:
                _logger.LogWarning(
                    "{Key}=On だが、所有者の読み取りのポリシーを認可サービスで数えられないので、内容の ABAC を有効にしない（門は閉じたまま）。"
                    + "認可サービスの gRPC 宛先（Services:AuthorizationServiceGrpc）と疎通を確かめる。",
                    ContentAbacOptions.ModeKey);
                break;
        }
        return next;
    }

    private Measurement<int> Observe()
    {
        var state = State;
        return new Measurement<int>(
            state == ContentAbacGateState.Open ? 1 : 0,
            new KeyValuePair<string, object?>(StateTag, StateLabel(state)));
    }

    public static string StateLabel(ContentAbacGateState state) => state switch
    {
        ContentAbacGateState.Disabled => "disabled",
        ContentAbacGateState.NotEvaluated => "not_evaluated",
        ContentAbacGateState.OwnerReadPolicyAbsent => "owner_read_policy_absent",
        ContentAbacGateState.OwnerReadPolicyUnknown => "owner_read_policy_unknown",
        ContentAbacGateState.Open => "open",
        _ => "unknown",
    };
}

// 常駐。構成が On のときだけ、起動時に評価し、開くまで一定周期で評価し直す（開いたら止まる）。
public sealed class ContentAbacGateHostedService(
    ContentAbacGate gate,
    IServiceScopeFactory scopeFactory,
    ILogger<ContentAbacGateHostedService> logger) : BackgroundService
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!gate.IsEnabledByConfiguration)
        {
            logger.LogInformation(
                "内容の ABAC は無効（{Key} 未設定 = Off）。有効にするには On を設定する（所有者の読み取りのポリシーを確かめてから開く）。",
                ContentAbacOptions.ModeKey);
            return;
        }

        using var timer = new PeriodicTimer(RetryInterval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var source = scope.ServiceProvider.GetRequiredService<IOwnerReadPolicyStatusSource>();
                if (await gate.EvaluateAsync(source, stoppingToken) == ContentAbacGateState.Open)
                    return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 門は閉じたまま（評価の途中の想定外の例外は「開いた」にしない）。
                logger.LogError(ex, "内容の ABAC の門の評価で想定外の例外が発生した。門は閉じたまま、次の周期で再評価する。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
