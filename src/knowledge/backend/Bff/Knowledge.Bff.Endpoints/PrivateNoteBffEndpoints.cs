using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Infrastructure.Foundation.Authz;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-19, FR-20, UC-11, SC-19, SC-20, ADR-0054 決定 4, #451: 個人資料（private-note）と
// Obsidian 連携設定の BFF エンドポイント。後段は DocumentService の `/private-notes*` 群である
// （[[IADR-0270]] が実装済み。本ファイルはそれを画面へ配る面だけを足す）。
//
// ── 認可の姿（本ファイルの核心。層ごとに分けて書く）
//
// 1. **認証は必須・ロールは要求しない。** 計画 `05_screens` は SC-19 / SC-20 を
//    「**全利用者が利用でき、表示範囲は本人が所有する個人資料に限る**」と定めており、
//    **管理者が他の利用者の個人資料・同期設定を扱う導線は設けない**と明記している。
//    書かれていない制限（ロール）を足さない（NFR-09 の暫定運用は認証だけをエッジへ課す）。
//
// 2. 🔴 **本人性の判定は後段が持つ。BFF は複製しない。** 後段は主体を**トークンからしか採らず**
//    （`PrivateNoteEndpoints.SubjectOf`）、台帳 `PrivateNote.OwnerId` / `SyncDevice.OwnerId` との
//    一致で絞り、**他者の資料・端末は 404 で存在ごと秘匿する**（403 を返すと他人の ID の実在が漏れる。
//    ADR-0036 D-04 / [[IADR-0009]]）。判定軸を BFF にも置くと 2 本になり、片方が壊れても気付けない
//    （`DocumentBffEndpoints` が所有者判定を持ち込まないと決めたのと同じ理由）。
//    **したがって BFF における本人絞りの実体は「利用者の資格情報を後段へ確実に渡すこと」である**
//    —— 落とすと後段は主体を決められず 401 になる（`Forwarding` を参照）。
//
// 3. **書き込みだけ ABAC の `write` スコープで前段を絞る**（ADR-0036 D-07 / #1010 / [[IADR-0272]]）。
//    deny なら 403。`POST /bff/documents` と同じ姿で、**write ポリシーが 1 件も無い環境では
//    書き込みが全件 403 になる**（deny-by-default の正しい帰結。配備時の登録が前提）。
//
// 4. 🔴 **読み取りには ABAC の前段を置かない。** 秘匿する相手が居ない（返すのは呼び出し者自身の
//    資料だけである）うえ、**解決済みスコープを適用する手段が無い** ——
//    `BffScopeResolver.Matches` は**文書属性**を見るが、一覧応答は台帳の投影であって属性を持たない。
//    属性を持たない資料へフィルタを当てると `MatchesAll` の安全側（キー欠落＝不一致）に落ち、
//    **利用者が自分の資料を 1 件も見られなくなる**。所有者スコープは後段が唯一の実施点である。
//
// 5. 🔴 **端末・トークン群にも ABAC の前段を置かない。** トークンは文書ではなく本人の資格情報であり、
//    計画 SC-20 は「**個別失効は端末紛失時の唯一の防御線であり必須**」と定めている。
//    失効を文書 ABAC ポリシーの有無に依存させると、**ポリシー未整備の環境で紛失端末を失効できない**。
//    安全側は「失効は通す」である（ABAC のスコープ対象でない資源をロール／所有者で絞る
//    [[IADR-0039]] と同じ切り分け）。
//
// ── 認可が及ばない範囲（過大申告しない）
// 3 の write ゲートは**個人資料の作成に対する封じ込め境界ではない**。同期プロトコル
// （`/private-notes/sync/*`）は同期トークンだけで資料を作れる —— ADR-0037 課題 2 が意図した
// 別系統の資格情報であり、本ファイルが作った穴ではない。write ゲートは画面経路の
// **多層防御の 1 枚**（[[IADR-0044]]）であって、それ以上ではない。
//
// ── 決定の記録: 上記 1〜5 と「認可が及ばない範囲」の非対称は [[IADR-0285]] が正本である
//    （波 3 監査の指摘で実装 ADR へ揃えた。本注釈は要約）。
//
// ── 載せなかった後段の口（理由は作業仕様書 §母集合 軸 1）
//   - `/private-notes/quotas/{ownerId}`（管理者の上限変更）… **載せる画面が計画に無い**。
//     SC-19 は管理者が他利用者の個人資料を扱う導線を明示的に禁じている。
//   - `/private-notes/sync/*`（同期プロトコル 4 件）… 資格情報が別系統（Bearer 同期トークン）で、
//     呼ぶのは Obsidian プラグインである。ブラウザ SPA は 1 度も呼ばない。
public static class PrivateNoteBffEndpoints
{
    public static IEndpointRouteBuilder MapPrivateNoteBffEndpoints(this IEndpointRouteBuilder app)
    {
        // FR-19, SC-19: 個人資料のライフサイクル（上の 1）。
        var notes = app.MapGroup("/bff/private-notes").WithTags("PrivateNotes BFF")
            .RequireAuthorization();

        // FR-20, SC-20: 同期端末とトークン（上の 5）。**群を分ける**のは、後段のパスが
        // `/private-notes/devices*` で別群だからであり、認可の強さが違うからではない。
        var devices = app.MapGroup("/bff/private-notes/devices").WithTags("PrivateNotes BFF")
            .RequireAuthorization();

        // ── SC-19: 一覧＋容量表示 ───────────────────────────────────
        // 削除済みも同じ一覧に載る（SC-19 の「削除済み」タブは同じ応答を絞って描く）。
        // **容量の内訳「うち削除済み」は画面が削除済み行の bytes を合算して出す** ——
        // 後段が持つのは台帳の行だけで、内訳という項目は存在しない（数え方を 2 つ持たない）。
        notes.MapGet("/", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ForwardAsync(HttpMethod.Get, "/private-notes/", null, httpFactory, http, ct))
            .WithName("BffPrivateNoteList").Produces<PrivateNoteListResponse>();

        // ── SC-19: 新規作成（本文なし。本文編集は Obsidian 経路のみ。ADR-0046 D-02）──────
        // 容量 100% では**新規作成だけ**が 507 で拒まれる（ADR-0037 決定 17）。
        // **本文を詰め替えず透過する** —— 507 の本文が SC-19 の固定文言（容量を空ける手段の案内）の
        // 根拠であり、詰め替えると画面が理由を出せない。
        notes.MapPost("/", (CreatePrivateNoteRequest req, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardIfWritableAsync(HttpMethod.Post, "/private-notes/", req, httpFactory, http, ct))
            .WithName("BffPrivateNoteCreate")
            .Produces<PrivateNoteDto>(StatusCodes.Status201Created);

        // ── SC-19: 論理削除（90 日は復元可・容量は空かない）───────────────────
        notes.MapDelete("/{id:guid}", (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardIfWritableAsync(HttpMethod.Delete, $"/private-notes/{id}", null,
                httpFactory, http, ct))
            .WithName("BffPrivateNoteSoftDelete").Produces<PrivateNoteDeletedResponse>();

        // ── SC-19: 復元（90 日以内。purge 済みは台帳ごと無く 404＝復元不可）──────────
        notes.MapPost("/{id:guid}/restore", (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardIfWritableAsync(HttpMethod.Post, $"/private-notes/{id}/restore", null,
                httpFactory, http, ct))
            .WithName("BffPrivateNoteRestore").Produces<PrivateNoteDto>();

        // ── SC-19: 完全削除（即時・復元不可）。**単票も一括も同じ口**（ids の要素数の差）──
        // SC-19 は「1 件ずつでは実用に耐えない」として一括を必須にしている。
        notes.MapPost("/purge", (PurgePrivateNotesRequest req, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardIfWritableAsync(HttpMethod.Post, "/private-notes/purge", req,
                httpFactory, http, ct))
            .WithName("BffPrivateNotePurge").Produces<PurgePrivateNotesResponse>();

        // ── SC-20: 露出 3 トグル（横断検索 / グラフ / AI 入力。既定 OFF・独立）─────────
        // 資料単位の設定であり、後段は保存するだけである（ON の消費側は [[IADR-0253]] 段 3 待ち）。
        notes.MapPut("/{id:guid}/exposure", (Guid id, UpdateExposureRequest req,
            IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ForwardIfWritableAsync(HttpMethod.Put, $"/private-notes/{id}/exposure", req,
                httpFactory, http, ct))
            .WithName("BffPrivateNoteExposure").Produces<PrivateNoteDto>();

        // ── SC-20: 端末一覧（トークンは平文もハッシュも載らない）──────────────────
        devices.MapGet("/", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ForwardAsync(HttpMethod.Get, "/private-notes/devices/", null, httpFactory, http, ct))
            .WithName("BffSyncDeviceList").Produces<List<SyncDeviceDto>>();

        // ── SC-20: トークン発行（**平文はこの応答で 1 回だけ**・有効期限 30 日）────────
        // **管理者承認を挟まない**（ADR-0037 決定 10・11。私物端末を認めるため本人管理とする）。
        devices.MapPost("/", (CreateSyncDeviceRequest req, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardAsync(HttpMethod.Post, "/private-notes/devices/", req, httpFactory, http, ct))
            .WithName("BffSyncDeviceIssue")
            .Produces<SyncTokenIssuedResponse>(StatusCodes.Status201Created);

        // ── SC-20: 手動再発行（旧トークンは即時無効。自動リフレッシュの口は存在しない）────
        // 期限切れ・失効済みの端末に対しても本人操作として再発行できる（回復経路はこれだけである）。
        devices.MapPost("/{id:guid}/reissue", (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardAsync(HttpMethod.Post, $"/private-notes/devices/{id}/reissue", null,
                httpFactory, http, ct))
            .WithName("BffSyncDeviceReissue").Produces<SyncTokenIssuedResponse>();

        // ── SC-20: 個別失効（**端末紛失時の唯一の防御線**）───────────────────────
        devices.MapDelete("/{id:guid}", (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardAsync(HttpMethod.Delete, $"/private-notes/devices/{id}", null,
                httpFactory, http, ct))
            .WithName("BffSyncDeviceRevoke");

        // ── SC-20: 全端末の一括失効（どの端末か特定できない場面の防御）──────────────
        devices.MapPost("/revoke-all", (IHttpClientFactory httpFactory, HttpContext http,
            CancellationToken ct) =>
            ForwardAsync(HttpMethod.Post, "/private-notes/devices/revoke-all", null,
                httpFactory, http, ct))
            .WithName("BffSyncDeviceRevokeAll").Produces<RevokeAllSyncDevicesResponse>();

        return app;
    }

    // FR-19, ADR-0036 D-07, #1010, [[IADR-0272]]: 書き込み経路は **write スコープ**で前段を絞る。
    //
    // **action を省略できない**のが #1010 の主眼である（既定値＝read だと、新しい経路を足した人が
    // 書き忘れることで認可が緩む）。read しか持たない主体はここで 403 になる。
    // **スコープの中身（文書条件）は使わない** —— 個人資料の可否は所有者で決まり、その判定は
    // 後段の台帳が持つ（冒頭の 2・4）。ここは「書いてよい主体か」の門だけを担う。
    private static async Task<IResult> ForwardIfWritableAsync(
        HttpMethod method, string path, object? body,
        IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
    {
        var scope = await BffScopeResolver.ResolveAsync(httpFactory, http, BffScopeAction.Write, ct);
        if (scope is null)
            return Results.Forbid(); // write 許可ポリシー無し＝変更不可（deny-by-default）

        return await ForwardAsync(method, path, body, httpFactory, http, ct);
    }

    // 後段（DocumentService）へ中継し、応答を**本文ごと**透過する。
    // 後段不達は 502 へ縮退する（空応答で隠さない —— 個人資料が「消えた」ように見えるのが最悪である）。
    private static async Task<IResult> ForwardAsync(
        HttpMethod method, string path, object? body,
        IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
    {
        var client = Forwarding(httpFactory, http);
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = JsonContent.Create(body, body.GetType());

        try
        {
            var resp = await client.SendAsync(req, ct);
            return await RelayAsync(resp, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    // 🔴 FR-19, ADR-0036: **利用者の資格情報を後段へ引き継ぐ。ここが本人絞りの実体である。**
    // 後段は主体を JWT からしか採らないため、転送を落とすと**誰の資料かを決められず 401 になる**
    // （緩む向きではないが、機能が丸ごと死ぬ）。BFF が別の資格情報（サービスアカウント）で
    // 呼ぶ形へ変えてはならない —— その瞬間に全利用者の資料が 1 つの主体の下に混ざる。
    private static HttpClient Forwarding(IHttpClientFactory httpFactory, HttpContext http)
    {
        var client = httpFactory.CreateClient("DocumentService");
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
        return client;
    }

    // 後段の応答（status・content-type・本文）をそのまま返す。
    // **507（容量上限）・409（パス重複 / 未削除）・404（存在秘匿）を保つために必須である** ——
    // 詰め替えると SC-19 の固定文言の根拠（使用量・上限・容量を空ける手段）が画面へ届かない。
    private static async Task<IResult> RelayAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return Results.NoContent();
        var content = await resp.Content.ReadAsStringAsync(ct);
        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        return Results.Content(content, contentType, statusCode: (int)resp.StatusCode);
    }
}
