using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-06, UC-01/UC-03/UC-07, SC-03: 文書の閲覧（読み取り）を集約する BFF エンドポイント。
// 横断検索（/bff/search）と同じく「ABAC スコープ解決（AuthorizationService）→ 文書取得（DocumentService）」
// を集約する。利用者スコープに合致しない文書、および不在の文書はいずれも 404 で応答し、存在を秘匿する
// （deny-by-default・IADR-0009/IADR-0038。「拒否」と「不在」を区別しない）。
// 本文（正規化 Markdown）は ABAC 判定後にオブジェクトストレージ（storage://）からサーバサイドで取得する
// （WikiService の読み取り経路と同一。未配備・未設定時はプレースホルダ本文へ縮退）。
public static class DocumentBffEndpoints
{
    public static IEndpointRouteBuilder MapDocumentBffEndpoints(this IEndpointRouteBuilder app)
    {
        // NFR-09, #656: **読み取り群にも認証を要求する。** 書き込み群（下記 `write`）だけが認可を持ち、
        // **読み取り群は無認証で到達できた**——同じファイルに `RequireAuthorization` が 6 個在るため、
        // ファイル単位の走査では気づけない形だった。
        // **ロールは群に置かない** —— 詳細・本文・版履歴は **SC-03**（文書詳細）であり、
        // SC-01 の出典クリックから**一般利用者が遷移する**（計画 `05_screens` の SC-01 §アクション）。
        var g = app.MapGroup("/bff/documents").WithTags("Documents BFF").RequireAuthorization();

        // 一覧: 権限内の文書のみ返す（deny-by-default。権限外文書は列挙しない）。
        //
        // NFR-09, SC-05, #656: **この口だけ管理者・運用者に絞る。** 計画 `05_screens`（2026-08-05 の裁定）は
        // 「**SC-05/06/07 = 閲覧は管理者・運用者**」と定めており、**呼び出し元は SC-05 の管理画面ただ 1 つ**
        // である（`sc05-documents/useDocumentAdmin.ts`。実測）。画面側は既に
        // `RequireRole anyOf={[Admin, Operator]}` で絞られているのに API が絞られていない状態で、
        // **#628 / #629 で 2 度直したのと同じ型**（画面は絞れているが API が誰でも通る）だった。
        // 群の `RequireAuthorization()` と AND 合成され、実効は admin ＋ operator になる（IADR-0128 決定 1）。
        g.MapGet("/", async (
            IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            // #1010: 一覧は読み取り経路 → read を明示する。
            var scope = await BffScopeResolver.ResolveAsync(httpFactory, http, BffScopeAction.Read, ct);
            if (scope is null)
                return Results.Ok(new List<DocumentDto>());

            var docs = await FetchListAsync(httpFactory, ct);
            var visible = docs.Where(d => IsManageable(d, scope)).ToList();
            return Results.Ok(visible);
        }).WithName("BffDocumentList").Produces<List<DocumentDto>>()
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        // 詳細: スコープ外・不在ともに 404（存在秘匿）。#1010: 読み取り経路 → read。
        g.MapGet("/{id:guid}", async (
            Guid id, IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var doc = await FetchAuthorizedAsync(id, BffScopeAction.Read, httpFactory, http, ct);
            return doc is null ? Results.NotFound() : Results.Ok(doc);
        }).WithName("BffDocumentDetail").Produces<DocumentDto>();

        // 版履歴: スコープ外・不在は 404。#1010: 読み取り経路 → read。
        g.MapGet("/{id:guid}/versions", async (
            Guid id, IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
        {
            var doc = await FetchAuthorizedAsync(id, BffScopeAction.Read, httpFactory, http, ct);
            if (doc is null)
                return Results.NotFound();

            var client = httpFactory.CreateClient("DocumentService");
            var versions = await client.GetFromJsonAsync<List<DocumentVersionDto>>(
                $"/documents/{id}/versions", ct);
            return Results.Ok(versions ?? []);
        }).WithName("BffDocumentVersions").Produces<List<DocumentVersionDto>>();

        // FR-06, UC-03, SC-03（#449）: **特定版の取得**。スコープ外・不在・版不在はいずれも 404。
        //
        // **計画 FR-06 の射程は「版の作成・一覧・取得」まで**であり（［2026-08-23 明確化］・
        // 環流 planning#473）、**版の復元（過去版へ戻す操作）は含まれない**。この口は取得だけを担う。
        //
        // 🔴 **応答は本文を含まない。本文の参照（`markdownUri`）も含まない**（#1011 / [[IADR-0290]]）。
        // `DocumentVersionDto` が持つのはタイトル・状態・属性・タグ・変更メモ・作成日時の
        // **メタデータのスナップショット**だけである。本文の実体は版ごとに保持されておらず
        // （オブジェクトキーが文書 ID から固定で決まり、再投入は同じキーを上書きする。参照 URI は
        // versionId を持たない）、**「その版の本文」を指せる値が存在しない**。
        // 以前は現行版の本文 URI がそのまま入っており、**呼び出し側が過去版の本文だと読み違えても
        // 応答から区別できなかった**ため、契約の側を事実へ揃えて落とした。
        //
        // 一覧（`/versions`）と同じ形で先に ABAC を判定する —— 判定を通さずに後段を引くと、
        // **閲覧できない文書の版メタデータが漏れる**。
        g.MapGet("/{id:guid}/versions/{version:int}", async (
            Guid id, int version, IHttpClientFactory httpFactory, HttpContext http,
            CancellationToken ct) =>
        {
            var doc = await FetchAuthorizedAsync(id, BffScopeAction.Read, httpFactory, http, ct);
            if (doc is null)
                return Results.NotFound();

            var client = httpFactory.CreateClient("DocumentService");
            var resp = await client.GetAsync($"/documents/{id}/versions/{version}", ct);
            // 後段の 404（その版が無い）はそのまま 404 で返す（存在秘匿の意味論と衝突しない）。
            if (!resp.IsSuccessStatusCode)
                return Results.NotFound();

            var snapshot = await resp.Content.ReadFromJsonAsync<DocumentVersionDto>(ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }).WithName("BffDocumentVersion").Produces<DocumentVersionDto>();

        // 本文（正規化 Markdown）: ABAC 判定後にオブジェクトストレージから読み取る。#1010: read。
        g.MapGet("/{id:guid}/content", async (
            Guid id, IHttpClientFactory httpFactory, IObjectStorageClient storage,
            HttpContext http, CancellationToken ct) =>
        {
            var doc = await FetchAuthorizedAsync(id, BffScopeAction.Read, httpFactory, http, ct);
            if (doc is null)
                return Results.NotFound();

            var markdown = await ReadMarkdownAsync(storage, doc.MarkdownUri, doc.Title, ct);
            return Results.Ok(new DocumentContentDto(doc.Id, doc.Title, markdown, doc.MarkdownUri));
        }).WithName("BffDocumentContent").Produces<DocumentContentDto>();

        // ---- SC-05, FR-06, UC-03, IADR-0041: 文書管理（書き込み） ----
        // 既存文書への操作（更新・公開・アーカイブ・削除）は、対象が利用者スコープ内であることを
        // 先に確認し、スコープ外・不在はいずれも 404 で秘匿する（閲覧できない文書は変更もできない）。
        //
        // **［#629］このグループ既定は「閲覧の下限」であり、書き込みの実効境界ではない。**
        // 計画 §SC-05（裁定 Q19）は**閲覧を管理者・運用者へ開き、破壊的操作は管理者限定を維持する**と
        // 定めている。したがって**個々の書き込み口へ `AdminOnly` を積む**（AND 合成で実効 admin のみ）。
        // **後段（`DocumentEndpoints`）にも同じ制限を置いてある** —— 片側だけだと
        // 「BFF 迂回で通る」か「画面だけ 403 になる」のどちらかが起きる（[[IADR-0044]] の多層防御）。
        var write = app.MapGroup("/bff/documents")
            .WithTags("Documents BFF")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        // 新規作成: スコープ解決済み（権限あり）の管理者が作成する。検証（タイトル必須=400）は透過する。
        //
        // FR-05, FR-06, ADR-0036 D-07 (#1010): **作成は write スコープで判定する。** 従前は
        // action 省略＝read で解決しており、read ポリシーしか持たない主体が文書を作成できた
        // （#993 と同型 —— 多層防御〔IADR-0044〕の ABAC 層が誤った action で評価されていた）。
        // write ポリシーが 1 件も無い環境では作成は全件 403 になる（deny-by-default の正しい帰結。
        // 配備時に write ポリシーの登録が前提になる）。
        write.MapPost("/", async (DocumentCreateRequest req, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
        {
            var scope = await BffScopeResolver.ResolveAsync(httpFactory, http, BffScopeAction.Write, ct);
            if (scope is null)
                return Results.Forbid(); // write 許可ポリシー無し＝作成不可（deny-by-default）

            var client = Forwarding(httpFactory, http);
            var resp = await client.PostAsJsonAsync("/documents", req, ct);
            return await RelayAsync(resp, ct);
        }).WithName("BffDocumentCreate").Produces<DocumentDto>(StatusCodes.Status201Created)
            // #629: 登録は管理者限定（計画の列挙「登録」）。
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // 更新（楽観ロック。ExpectedVersion 不一致=409 透過）。
        write.MapPut("/{id:guid}", (Guid id, DocumentUpdateRequest req, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardIfInScope(id, HttpMethod.Put, $"/documents/{id}", req, httpFactory, http, ct))
            // #629: 編集は管理者限定（計画の列挙「文書の編集」）。
            .WithName("BffDocumentUpdate").RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // 公開（取り込み・Wiki 同期をトリガ）。
        write.MapPost("/{id:guid}/publish", (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardIfInScope(id, HttpMethod.Post, $"/documents/{id}/publish", null, httpFactory, http, ct))
            // #629: 公開は管理者限定。計画の列挙に名前が無いため planning#299 の基準を当てはめた
            // （作業仕様書 §判断 1。後段 `DocumentEndpoints` の同じ口に理由の全文がある）。
            .WithName("BffDocumentPublish").RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // アーカイブ（非公開化）。
        write.MapPost("/{id:guid}/archive", (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardIfInScope(id, HttpMethod.Post, $"/documents/{id}/archive", null, httpFactory, http, ct))
            // #629: アーカイブは管理者限定（公開と同じ基準。可視性を落とすので (a) すら満たさない）。
            .WithName("BffDocumentArchive").RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // 削除（下流の Wiki 同期へ伝播）。
        write.MapDelete("/{id:guid}", (Guid id, IHttpClientFactory httpFactory,
            HttpContext http, CancellationToken ct) =>
            ForwardIfInScope(id, HttpMethod.Delete, $"/documents/{id}", null, httpFactory, http, ct))
            // #629: 削除は管理者限定（計画の列挙「文書の削除」）。
            .WithName("BffDocumentDelete").RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        return app;
    }

    // SC-05: 対象文書が利用者スコープ内のときのみ後段へ書き込みを転送する。スコープ外・不在は 404 秘匿。
    // 検証（400）・楽観ロック競合（409）は後段の応答を透過する。
    // NFR/#179・IADR-0045: このプリフライト GET（スコープ確認）を「後段に認可がある（IADR-0044）から冗長」
    // として削除してはならない。IADR-0044 が後段に課したのはロール認可のみで、文書単位の ABAC スコープ
    // 照合はこの BFF が唯一の実施点（IADR-0041）。削除するとスコープ外の admin/operator が閲覧不可の文書を
    // 変更でき、存在秘匿（IADR-0009）も破れる。往復削減は実測で正当化された上で IADR-0045 の代替案に従う。
    // FR-05, ADR-0036 D-07 (#1010): **書き込みプリフライトは write スコープで判定する。**
    // 従前は read スコープ（「閲覧できるか」）で「変更してよいか」を判定しており、read ポリシー
    // しか持たない主体が変更できた（#993 と同型）。write スコープの文書条件をそのまま適用し、
    // 条件外・不在はいずれも 404 で秘匿する（既存のステータス形は変えない）。計画の write 規則
    // （doc.owner ∈ { ${current_user} }）を満たす主体は read の所有者ベース分岐も満たすため、
    // 「閲覧できない文書を書き換えられる」逆転は計画上生じない。
    private static async Task<IResult> ForwardIfInScope(
        Guid id, HttpMethod method, string path, object? body,
        IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
    {
        var doc = await FetchAuthorizedAsync(id, BffScopeAction.Write, httpFactory, http, ct);
        if (doc is null)
            return Results.NotFound(); // write スコープ外の文書は変更できない（存在秘匿）

        var client = Forwarding(httpFactory, http);
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = JsonContent.Create(body, body.GetType());

        var resp = await client.SendAsync(req, ct);
        return await RelayAsync(resp, ct);
    }

    // 後段の応答（status・content-type・本文）をそのまま返す（検証 400・競合 409・不在 404 を保つ）。
    private static async Task<IResult> RelayAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return Results.NoContent();
        var content = await resp.Content.ReadAsStringAsync(ct);
        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        return Results.Content(content, contentType, statusCode: (int)resp.StatusCode);
    }

    // FR-05: 利用者の資格情報を後段へ引き継ぐ DocumentService クライアントを生成する。
    private static HttpClient Forwarding(IHttpClientFactory httpFactory, HttpContext http)
    {
        var client = httpFactory.CreateClient("DocumentService");
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);
        return client;
    }

    // 文書を取得し、利用者スコープに合致するときのみ返す（deny/不在/解決不能 → null＝404 秘匿）。
    // FR-19, ADR-0036 D-08 (#1009): SC-05 文書管理の経路に個人資料を出さない。
    //
    // 🔴 **ABAC のスコープ判定だけでは足りない。** `BffScopeResolver.Matches` は
    // `scope.Filters` に**現れたキーだけ**を見るため、`doc_scope` を条件に持たないポリシー
    // （＝現行の全ポリシー。ADR-0054 は 2026-08-22 新設で実データ 0 件）では、この属性は
    // **判定に一切効かない**。個人資料は `confidentiality=restricted` で作られるので、
    // 「restricted 取扱者は全区分を読める」型のポリシーに**そのまま合致する**。
    //
    // ADR-0036 D-08 は「管理者・運用者は平時、非公開の個人資料を**一切閲覧できない**」と定め、
    // 第三者の発動経路を管理者を含めて設けないとしている。ポリシー側に `doc_scope` を足すのが
    // 本筋だが、**それは配備データに依存する統制であり、コードは無防備なままになる**
    // （既存文書は `doc_scope` を持たないため、ポリシーで名指した瞬間に一斉に不可視化する
    // 副作用もある）。ここで構造的に落とす。
    //
    // **除外は所有者を問わず一律である。** 個人資料は SC-19（`/private-notes`。本人スコープ）が
    // 持ち、SC-05 は組織文書の管理画面である。所有者判定をここへ持ち込むと、判定軸が 2 本になり
    // 片方が壊れても気付けない。**判定は集合帰属で書く**（「organization でない」ではない ——
    // 属性を持たない既存文書が全部 個人資料 に化ける。ADR-0054 決定 5）。
    private static bool IsManageable(DocumentDto doc, BffAccessScope scope)
        => BffScopeResolver.Matches(doc.Attributes, scope) && !IsPrivateNote(doc);

    // `DocumentAttributes`（DocumentService）はユニット外から参照できないため、判定を持つ
    // （GraphService が `GraphDocumentScope` を持つのと同じ理由・同じ形）。
    private static bool IsPrivateNote(DocumentDto doc)
        => doc.Attributes.TryGetValue("doc_scope", out var scope)
            && string.Equals(scope, "private-note", StringComparison.OrdinalIgnoreCase);

    // #1010: action は呼び出し元の経路の意味で選ぶ —— 読み取り GET（詳細・版履歴・本文）は
    // BffScopeAction.Read、書き込みプリフライト（ForwardIfInScope）は BffScopeAction.Write。
    private static async Task<DocumentDto?> FetchAuthorizedAsync(
        Guid id, string action, IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
    {
        var scope = await BffScopeResolver.ResolveAsync(httpFactory, http, action, ct);
        if (scope is null)
            return null;

        var client = httpFactory.CreateClient("DocumentService");
        HttpResponseMessage resp;
        try
        {
            resp = await client.GetAsync($"/documents/{id}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }

        if (!resp.IsSuccessStatusCode)
            return null; // 404 等（不在）は秘匿し区別しない

        var doc = await resp.Content.ReadFromJsonAsync<DocumentDto>(ct);
        if (doc is null || !IsManageable(doc, scope))
            return null; // スコープ外・個人資料は不在と同じ 404

        return doc;
    }

    private static async Task<List<DocumentDto>> FetchListAsync(
        IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("DocumentService");
        try
        {
            return await client.GetFromJsonAsync<List<DocumentDto>>("/documents", ct) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    // FR-06: 正規化 Markdown をオブジェクトストレージ（storage://）から取得する。ストレージ未配備・
    // URI 未設定時はプレースホルダ本文へ縮退する（WikiService.StorageMarkdownReader と同じ縮退方針）。
    private static async Task<string> ReadMarkdownAsync(
        IObjectStorageClient storage, string? markdownUri, string title, CancellationToken ct)
    {
        if (storage.CanResolve(markdownUri))
            return await storage.GetTextAsync(markdownUri!, ct);
        return $"# {title}\n\nコンテンツは {markdownUri ?? "(未設定)"} から取得します。";
    }
}

// SC-05, FR-06, UC-03: 文書管理の書き込みリクエスト（BFF→DocumentService。JSON 互換）。
public record DocumentCreateRequest(
    string Title,
    string? OriginalUri,
    string? ContentType,
    Dictionary<string, string>? Attributes,
    List<string>? Tags);

public record DocumentUpdateRequest(
    string Title,
    Dictionary<string, string>? Attributes,
    List<string>? Tags,
    int? ExpectedVersion = null,
    string? ChangeNote = null);
