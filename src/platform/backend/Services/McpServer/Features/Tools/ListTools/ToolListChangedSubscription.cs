using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using McpServer.Domain;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.Features.Tools.ListTools;

// FR-16, ADR-0024 §2, [[IADR-0651]] (#2142): **公開ツールの集合が変わったことを、`subscriptions/listen` の流れで
// `notifications/tools/list_changed` として届ける。**
//
// 本サーバーは Streamable HTTP を**無状態**（SDK 2.2 の既定。セッションの付着を要さない）で動かしている。無状態では要求の外から
// 送る通知（セッション全体への list_changed）は捨てられる。届けられるのは、2026-07-28 の改訂の `subscriptions/listen`
// （クライアントが開いて保つ要求）の応答の流れの上だけである。SDK の既定の listen は、要求ごとに作られる無状態のサーバーでは
// 集合の変化を拾えないので、**listen の処理を自前で持つ**（SDK の `SubscriptionsListenHandler` の契約どおり）。
// - 最初に 1 回だけ受付の通知（`notifications/subscriptions/acknowledged`）を送り、扱う種類だけを返す（ここでは tools の list_changed だけ）。
// - 以後、カタログの版が進むたびに list_changed を送る。各通知の `_meta` に listen の要求 ID を付ける（分離のため）。
// - 取り消し（切断）で抜け、空の結果を返す。
// 一覧そのもの（利用者ごとの ABAC・公開構成）は従来どおり `tools/list` が組む。通知は「引き直してよい」の合図だけである。
//
// ［#2315 の監査］長命の接続を枯らさないための上限（`ToolListChangedListenLimits`）:
// - 1 本の listen は最長 `MaxLifetime`（既定 10 分）で空の結果を返して閉じる。クライアントは開き直す（開き直すたびに
//   `/mcp` の認証・認可を通り直すので、失効したトークン・外れたロールで通知が届き続ける時間もこの長さで頭打ちになる）。
// - 同時に開ける本数は、主体ごと `MaxPerSubject`（既定 4）・プロセス全体 `MaxTotal`（既定 256）。超えた要求は
//   `InvalidRequest` で断る（開いている listen は切らない）。
// 通知は「引き直してよい」の合図だけで、ツール一覧そのものは運ばない（一覧は `tools/list` が利用者ごとに組む）。
public sealed class ToolListChangedListenLimits
{
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromMinutes(10);
    public int MaxPerSubject { get; init; } = 4;
    public int MaxTotal { get; init; } = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _perSubject = new(StringComparer.Ordinal);
    private int _total;

    /// <summary>1 本を確保する。上限を超えるなら false。</summary>
    public bool TryAcquire(string subject)
    {
        lock (_gate)
        {
            var mine = _perSubject.GetValueOrDefault(subject);
            if (_total >= MaxTotal || mine >= MaxPerSubject) return false;
            _perSubject[subject] = mine + 1;
            _total++;
            return true;
        }
    }

    public void Release(string subject)
    {
        lock (_gate)
        {
            if (_perSubject.TryGetValue(subject, out var mine))
            {
                if (mine <= 1) _perSubject.Remove(subject);
                else _perSubject[subject] = mine - 1;
                _total--;
            }
        }
    }
}

public static class ToolListChangedSubscription
{
    public static async ValueTask<EmptyResult> ListenAsync(
        RequestContext<SubscriptionsListenRequestParams> request, ToolCatalog catalog,
        ToolListChangedListenLimits limits, CancellationToken ct)
    {
        // 主体は `/mcp` の認証が決めた利用者の `sub`（JwtBearer の既定の写像で NameIdentifier になる）。表示名（preferred_username）は
        // 一意でも必須でもないので枠の鍵にしない。🔴 ［#2315 のレビュー］**sub の無いトークンは listen 自体を断る**
        // （トークンの検証は sub を必須にしていない。共有の枠を作ると、sub の無い利用者どうしが互いを締め出せる）。
        var subject = request.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? request.User?.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
            throw new McpProtocolException(
                "subscriptions/listen には利用者を特定できるトークン（sub）が要ります。", McpErrorCode.InvalidRequest);
        if (!limits.TryAcquire(subject))
            throw new McpProtocolException(
                "subscriptions/listen の同時接続の上限に達しています。開いている listen を閉じてから開き直してください。",
                McpErrorCode.InvalidRequest);
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lifetime.CancelAfter(limits.MaxLifetime);
            return await ListenWithinAsync(request, catalog, lifetime.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 最長の寿命に達した。空の結果で閉じ、クライアントに開き直させる（認証を通り直させる）。
            return new EmptyResult();
        }
        finally
        {
            limits.Release(subject);
        }
    }

    private static async ValueTask<EmptyResult> ListenWithinAsync(
        RequestContext<SubscriptionsListenRequestParams> request, ToolCatalog catalog, CancellationToken ct)
    {
        var wantsTools = request.Params?.Notifications?.ToolsListChanged == true;
        var subscriptionId = JsonSerializer.SerializeToNode(request.JsonRpcRequest.Id, McpJsonUtilities.DefaultOptions);

        // 受付より前に購読しておく（受付と購読の間に進んだ版を取りこぼさない）。
        var changes = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
        void OnChanged(int version) => changes.Writer.TryWrite(version);
        if (wantsTools) catalog.Changed += OnChanged;
        try
        {
            var ack = JsonSerializer.SerializeToNode(new SubscriptionsAcknowledgedNotificationParams
            {
                Notifications = new SubscriptionsListenNotifications { ToolsListChanged = wantsTools ? true : null },
            }, McpJsonUtilities.DefaultOptions)!.AsObject();
            ack["_meta"] = new JsonObject { [MetaKeys.SubscriptionId] = subscriptionId?.DeepClone() };
            await request.Server.SendMessageAsync(new JsonRpcNotification
            {
                Method = NotificationMethods.SubscriptionsAcknowledgedNotification,
                Params = ack,
            }, ct);

            // ［#2315 のレビュー］tools を求めない listen は、受付の後にすぐ閉じる（送る通知が無いのに接続を保持しない）。
            if (!wantsTools)
                return new EmptyResult();

            while (await changes.Reader.WaitToReadAsync(ct))
            {
                // 続けて進んだ版は 1 回の合図にまとめる（クライアントは引き直すだけなので、回数に意味は無い）。
                while (changes.Reader.TryRead(out _)) { }
                await request.Server.SendMessageAsync(new JsonRpcNotification
                {
                    Method = NotificationMethods.ToolListChangedNotification,
                    Params = new JsonObject
                    {
                        ["_meta"] = new JsonObject { [MetaKeys.SubscriptionId] = subscriptionId?.DeepClone() },
                    },
                }, ct);
            }
            return new EmptyResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        finally
        {
            if (wantsTools) catalog.Changed -= OnChanged;
        }
    }
}
