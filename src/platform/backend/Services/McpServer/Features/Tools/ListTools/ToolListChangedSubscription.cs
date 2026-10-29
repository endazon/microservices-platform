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
public static class ToolListChangedSubscription
{
    public static async ValueTask<EmptyResult> ListenAsync(
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

            if (!wantsTools)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new EmptyResult();
            }

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
