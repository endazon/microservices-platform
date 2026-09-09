using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Platform.Shared.Contracts.Grpc.Notification.V1;

namespace NotificationService.Features.Notifications.Accept;

// FR-19, FR-20, FR-21, FR-22, NFR-09, NFR-16, NFR-19, UC-11, ADR-0004, ADR-0029, ADR-0037 決定 6・17・18,
// ADR-0045 決定 8, ADR-0075, [[IADR-0215]] 決定 2・3・4, [[IADR-0270]] 決定 6, [[IADR-0371]],
// [[IADR-0379]] 決定 4・5, [[IADR-0398]] 決定 1・7, [[IADR-0401]], [[IADR-0408]], [[IADR-0417]],
// [[IADR-0419]] (#1255): 通知の受け口の **east-west gRPC 面**。
//
// 🔴 **本体は持たない。** `NotificationIngress.AcceptAsync` を呼ぶだけであり、REST の
// `POST /internal/notifications` と**同じ関数**を通る（検証器も重複判定も 2 つにしない。
// [[IADR-0402]] 決定 6 / [[IADR-0408]] / [[IADR-0417]] 決定 5 と同じ形）。
// ここに在るのは「輸送の言葉へ写すこと」だけである。
//
// 🔴 **ServiceCaller を要求する。** REST の受け口は**認証を課していない**（[[IADR-0017]] /
// [[IADR-0026]] の内部 API 扱い。呼び出し元は利用者文脈を持たない定期処理である）が、
// gRPC 面は [[IADR-0379]] 決定 4 に従い realm ロール `platform-service` を要求する ——
// **権限が狭まる向き**である（[[IADR-0401]] 決定 1 / [[IADR-0417]] 決定 4 と同じ）。
// 🔴 **利用者トークンでは開かない**（confused deputy の防止）——「認証さえあれば通る」形にすると、
// 転送された管理者トークンで s2s の面が開く。**REST の口は残す**（並走中の正は REST）。
//
// 🔴 **status への写像は REST の状態コードと 1:1 である。**
//   検証違反 → `INVALID_ARGUMENT`（REST の 400 = `ValidationProblem`）。それ以外は成功。
//   **成功以外を成功に見せない** —— 呼び出し元は結末を計器（`notification.dispatch.total`）へ
//   載せており、ここで握ると「送れなかったぶん」が数えられなくなる（ADR-0045 決定 8 の発火側の対）。
//
// 🔴 **鍵ごとの検証本文（形 β）は輸送を跨いで再現しない**（[[IADR-0398]] 決定 1）。
//   REST は `ValidationProblem` で**鍵 5 つ**を返し得るが、gRPC の status に対応する構造は無い。
//   守るのは「不正なペイロードを 1 件も永続化しない」「成功に見せない」であって、本文の形の一致ではない
//   （[[IADR-0417]] 決定 9 の「元の HTTP 状態番号は輸送を跨いで再現できない」と同じ）。
//   運用が原因を追えるように、**鍵と本文を status の detail へ連結して載せる** ——
//   検証メッセージは**静的な文字列だけ**であり（`NotificationIngressValidator` の 7 定数）、
//   利用者の資料名も本文も混ざらない。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class NotificationIngressGrpcService(NotificationIngress ingress)
    : Pb.NotificationIngress.NotificationIngressBase
{
    public override async Task<Pb.AcceptResponse> Accept(Pb.AcceptRequest request, ServerCallContext context)
    {
        var outcome = await ingress.AcceptAsync(ToRequest(request), context.CancellationToken);

        if (!outcome.IsValid)
            throw new RpcException(new Status(StatusCode.InvalidArgument, Describe(outcome.Errors!)));

        return new Pb.AcceptResponse { Duplicate = outcome.IsDuplicate };
    }

    // 🔴 **proto3 の「未指定」を REST の null へ戻す写し**（[[IADR-0408]] 決定 3 / [[IADR-0419]] 決定 2）。
    // `Has*` を読まずに素の値を読むと、未設定が `0` に化けて**別の事実**になる:
    //   - `Count` … 「件数 0 件」の通知が新規として積まれる（検証は通るので**例外は 1 つも起きない**。
    //     割れるのは重複判定 `n.Count == request.Count` だけであり、静かに二重通知が出る）。
    //   - `ThresholdPercent` … 同上。80% / 95% の区別が「0%」に潰れる。
    // 時刻は message なので未設定が `null` に読める（`Timestamp?`）——
    // `OccurredAt` の欠落はそのまま検証器の「occurredAt は必須である。」へ落ちる。
    internal static NotificationIngressRequest ToRequest(Pb.AcceptRequest request) => new(
        request.Subject,
        request.Kind,
        request.OccurredAt?.ToDateTimeOffset(),
        request.HasCount ? request.Count : null,
        request.HasThresholdPercent ? request.ThresholdPercent : null,
        request.Deadline?.ToDateTimeOffset());

    // 形 β の辞書（鍵 → 本文の配列）を 1 本の文字列へ畳む。**鍵の並びは検証器の宣言順**であり、
    // `ToDictionary()` がそれを保つ（[[IADR-0398]] 決定 1）。
    private static string Describe(IDictionary<string, string[]> errors) =>
        string.Join(" / ", errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}"));
}
