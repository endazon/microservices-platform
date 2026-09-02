namespace Platform.Bff.Foundation.Endpoints;

// FR-22, UC-11, ADR-0037 決定 6・17・18, ADR-0045, IADR-0215 / IADR-0267 / IADR-0346, #600:
// 利用者本人へのアプリ内通知の BFF 集約。後段 `NotificationService` の `/notifications*` へ中継する。
//
// **前段（契約・orval 生成フック・ベル UI）と後段（サービス・発火の結線・配備）は先に入っており、
// 本ファイルはその真ん中の 1 本である。** これが入るまで画面には何も出なかった。
//
// ── 認可の姿（層ごとに分けて書く）
//
// 1. **認証は必須・ロールは要求しない**（契約の `x-roles: []`）。通知は全利用者が受け取るものであり、
//    ロールを足すと**一般利用者が自分の通知を読めなくなる**。
//    **逆に管理者へ他人の通知を見せる口も作らない** —— 絞るのは役割ではなく主体である。
//
// 2. 🔴 **本人絞りの実体は「資格情報を後段へ確実に渡すこと」である。BFF は判定を複製しない。**
//    後段は主体を**トークンからしか採らず**（`NotificationSubject.Of(http.User)`）、
//    台帳の `Subject` との一致で絞る。**BFF が主体をパラメータで渡す形にしてはならない** ——
//    その瞬間に「他人の ID を入れたらどうなるか」という面ができる。
//    転送を落とすと後段は主体を決められず 401 になる（緩む向きではないが機能が丸ごと死ぬ）。
//    BFF セッション方式（ADR-0032 / IADR-0251 / IADR-0273）では
//    `SessionTokenPropagationMiddleware` が Cookie セッションのアクセストークンを
//    `Authorization` へ載せるため、ここはその結果を読むだけでよい（新しい方式を作らない）。
//
// 3. 🔴 **ABAC の前段を置かない。** `BffScopeResolver` が見るのは**文書属性**であり、通知は文書ではない。
//    スコープを当てると `MatchesAll` の安全側（キー欠落＝不一致）へ落ち、
//    **利用者が自分の通知を 1 件も見られなくなる**。所有者スコープは後段が唯一の実施点である
//    （`PrivateNoteBffEndpoints` の読み取り側と同じ切り分け）。
//
// 4. 🔴 **応答は透過する。状態コードを作り替えない。** 後段は「存在しない」と「本人のものでない」を
//    区別せず **404** を返す（存在秘匿。IADR-0009 / ADR-0004）。403 や 200 へ変換すると
//    **存在秘匿が BFF 層で破れる**。後段不達は **502** へ縮退する ——
//    空の 200 で隠すと「通知が 0 件になった」ように見え、利用者は期限を見落とす。
//
// 5. **`limit` のクランプを BFF に置かない。** 後段の `NotificationStore.ListAsync` が
//    `Math.Clamp(limit ?? DefaultListLimit, 1, MaxListLimit)` を持つ（既定 50 / 上限 100 は
//    `NotificationOptions`）。ここへ 2 つ目のクランプを置くと、設定を変えたときに BFF 側だけ
//    古い上限で切る形になる（数え方を 2 つ持たない）。**そのまま後段のクエリへ載せる。**
public static class NotificationBffEndpoints
{
    /// <summary>後段（NotificationService）の named HttpClient 名。Program.cs の登録と一致させる。</summary>
    public const string ClientName = "NotificationService";

    public static IEndpointRouteBuilder MapNotificationBffEndpoints(this IEndpointRouteBuilder app)
    {
        // 上の 1: 認証だけを要求する（`RequireAuthorization()` にロール要件を足さない）。
        var g = app.MapGroup("/bff/notifications")
            .WithTags("Notifications BFF")
            .RequireAuthorization();

        // FR-22: 本人宛の通知一覧（新しい順）＋未読件数。
        // **クエリは型付きで受けて 2 つだけを載せ替える**（生のクエリ文字列を素通しにしない ——
        // 後段の面に無いパラメータを無検査で渡す口を作らないため）。
        g.MapGet("", (bool? unreadOnly, int? limit,
            IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            ProxyAsync(f, h, HttpMethod.Get, BuildListPath(unreadOnly, limit), ct))
            .WithName("BffNotificationList");

        // FR-22: 通知 1 件の既読化。**冪等**（既読へもう一度呼んでも後段が 200 を返し、それを透過する）。
        // 本人の通知でなければ後段が 404 を返す（上の 4）。
        g.MapPost("/{id:guid}/read", (Guid id,
            IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            ProxyAsync(f, h, HttpMethod.Post, $"/notifications/{id}/read", ct))
            .WithName("BffNotificationMarkRead");

        return app;
    }

    // 後段のクエリを組み立てる。**指定されたものだけを載せる** ——
    // 既定値を BFF が埋めると、後段の既定（`NotificationOptions`）と二重管理になる（上の 5）。
    private static string BuildListPath(bool? unreadOnly, int? limit)
    {
        var parts = new List<string>(2);
        if (unreadOnly is not null)
            parts.Add($"unreadOnly={(unreadOnly.Value ? "true" : "false")}");
        if (limit is not null)
            parts.Add($"limit={limit.Value}");

        return parts.Count == 0 ? "/notifications" : $"/notifications?{string.Join('&', parts)}";
    }

    // 後段へ中継し、応答（状態・Content-Type・本文）をそのまま返す。
    // 通知の応答は小さい（既定 50 件・自由文なし）ため一括読み込みでよい。
    private static async Task<IResult> ProxyAsync(
        IHttpClientFactory httpFactory, HttpContext http, HttpMethod method, string path, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(ClientName);
        using var req = new HttpRequestMessage(method, path);

        // 🔴 上の 2: 資格情報の転送。これが本人絞りの実体である。
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            req.Headers.TryAddWithoutValidation("Authorization", auth);

        try
        {
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            return Results.Content(body, contentType, statusCode: (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 上の 4: 不達は 502。空の 200 で隠さない。
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
