using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Security.Claims;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// FR-16, FR-05, FR-09, UC-09, SC-12, ADR-0062 決定 2・3, ADR-0036 D-01・D-02, IADR-0385 (#1242):
// **本物の解決器**に対して AuthorizationService の HTTP をスタブし、認可スコープの読み方を固定する。
//
// 🔴 **本クラスが無かったことが #1242 の原因である。** 従前は `StubRegistrarAttributeResolver`
// （ヘッダで集合を注入する）経由の経路テストしか無く、**「スコープをどう読むか」は 1 本も
// 試験されていなかった**。判定の入力を作る側が試験されないと、fail-open は緑のまま通る。
//
// ここで固定するのは**読み方**である。疎通（実 AuthorizationService への到達）は測らない。
[Trait("TestKind", "Unit")]
public class AuthorizationServiceRegistrarAttributesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Registrar = "tanaka";

    // `GET /authz/users` の応答（登録者 1 名）。タグは既定で持たせない。
    private static string Directory(string? tags = null)
    {
        var tagAttr = tags is null ? string.Empty : $",\"tags\":\"{tags}\"";
        return "[{\"id\":\"u1\",\"username\":\"" + Registrar + "\","
            + "\"displayName\":\"田中\",\"enabled\":true,\"roles\":[\"platform-admin\"],"
            + "\"attributes\":{\"department\":\"engineering\"" + tagAttr + "}}]";
    }

    private static async Task<RegistrarAssignableAttributes> ResolveAsync(string scopeJson, string? tags = null)
    {
        var handler = new StubHandler()
            .Get("/authz/users", Directory(tags))
            .Post("/authz/scope", scopeJson);

        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, Registrar)], "test")),
            },
        };

        var resolver = new AuthorizationServiceRegistrarAttributes(
            new StubFactory(handler), accessor,
            NullLogger<AuthorizationServiceRegistrarAttributes>.Instance);

        return await resolver.ResolveAsync(Ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 🔴 受け入れ基準 1（陰性対照 / #1242 の本体）
    // ─────────────────────────────────────────────────────────────────────────
    //
    // **`confidentiality` のフィルタを持たない登録者は、いかなる機密区分も配れない。**
    // 入力は `AbacEvaluator` が ADR-0036 の所有者 `read` ポリシー 1 本に対して実際に作る形である
    // （その形そのものは `AbacEvaluatorTests.ResolveScope_OwnerOnlyReadPolicy_...` が固定する）。
    //
    // 🔴 **変異試験（実測。出力は PR 本文にある）**: `ReadAssignableConfidentiality` を旧実装
    // （`AllowedFilters` から `confidentiality` を引き、`filter is null` なら無制限）へ戻すと、
    // **次の 5 本が落ちる** —— 陰性対照 3 本（本テスト /
    // `所有者分岐だけでは_restricted_を配れない` / `所有者と機密区分の連言は数えない`）に加え、
    // `分岐を運ばない発行者で他キーが混ざる_union_は読まない` と
    // `階段の登録者は自分より広い区分を配れない`（**据え置きの `AllowedFilters` が空でも
    // 分岐は機密区分で絞っている**形。旧実装はこれも無制限と読む）。
    // **陽性対照はどれも緑のまま通る**（＝「常に空集合を返す」実装で通る試験ではない）。
    // 🔴 **本クラスの総数は書かない** —— テストが増えるたびに腐る導出値である。
    [Fact]
    public async Task 所有者ベースの分岐だけにマッチする登録者は機密区分を配れない()
    {
        var scope = await ResolveAsync("""
            {"userId":"tanaka",
             "allowedFilters":[{"key":"owner","allowedValues":["${current_user}"]}],
             "granted":true,
             "branches":[{"name":"dev: 所有者は自分の文書を読める",
                          "filters":[{"key":"owner","allowedValues":["tanaka"]}]}]}
            """);

        scope.Available.Should().BeTrue("スコープは引けている（引けなかったのとは違う）");
        scope.ClearanceUnrestricted.Should().BeFalse(
            "フィルタの不在は『制約なし』ではない —— その軸で許可する根拠が無いだけである");
        scope.Clearance.Should().BeEmpty();
    }

    // 上の帰結を後段の判定まで通して見る（**この登録者は `restricted` を配れない**）。
    [Fact]
    public async Task 所有者分岐だけでは_restricted_を配れない()
    {
        var registrar = await ResolveAsync("""
            {"userId":"tanaka",
             "allowedFilters":[{"key":"owner","allowedValues":["${current_user}"]}],
             "granted":true,
             "branches":[{"name":"所有者","filters":[{"key":"owner","allowedValues":["tanaka"]}]}]}
            """);

        var errors = ServiceAccountAttributeSubset.Validate(
            "sa-escalation",
            new Dictionary<string, string> { ["clearance"] = "restricted" },
            registrar);

        errors.Should().ContainSingle().Which.Should().Contain("restricted").And.Contain("ありません");
    }

    // 🔴 **所有権が混ざった連言は数えない。** 「自分が持つ restricted 文書を読める」は
    // 「restricted を読める」ではない —— サービスアカウントは登録者の所有権を継がない。
    [Fact]
    public async Task 所有者と機密区分の連言は数えない()
    {
        var scope = await ResolveAsync("""
            {"userId":"tanaka",
             "allowedFilters":[{"key":"owner","allowedValues":["${current_user}"]},
                               {"key":"confidentiality","allowedValues":["restricted"]}],
             "granted":true,
             "branches":[{"name":"自分の restricted",
                          "filters":[{"key":"owner","allowedValues":["tanaka"]},
                                     {"key":"confidentiality","allowedValues":["restricted"]}]}]}
            """);

        scope.ClearanceUnrestricted.Should().BeFalse();
        scope.Clearance.Should().BeEmpty("所有権を継がない相手へ restricted を配る根拠にはならない");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 受け入れ基準 3（陽性対照・回帰）: 現 seed と同型の階段では従来どおり配れる
    // ─────────────────────────────────────────────────────────────────────────
    //
    // **陽性対照が無いと「常に空集合を返す」実装が上の陰性を全部通す。**
    [Fact]
    public async Task 階段ポリシーでは読める機密区分がそのまま配れる集合になる()
    {
        var scope = await ResolveAsync("""
            {"userId":"tanaka",
             "allowedFilters":[{"key":"confidentiality","allowedValues":["public","internal"]}],
             "granted":true,
             "branches":[{"name":"dev: public 取扱者は public を読める",
                          "filters":[{"key":"confidentiality","allowedValues":["public"]}]},
                         {"name":"dev: internal 取扱者は public/internal を読める",
                          "filters":[{"key":"confidentiality","allowedValues":["public","internal"]}]}]}
            """);

        scope.ClearanceUnrestricted.Should().BeFalse();
        scope.Clearance.Should().BeEquivalentTo(["public", "internal"]);
    }

    // 分岐の union は**重複を畳む**（同じ値が 2 本の分岐に出る。上の seed 形がまさにそれ）。
    [Fact]
    public async Task 階段の登録者は自分より広い区分を配れない()
    {
        var registrar = await ResolveAsync("""
            {"userId":"tanaka","allowedFilters":[],"granted":true,
             "branches":[{"name":"internal 段",
                          "filters":[{"key":"confidentiality","allowedValues":["public","internal"]}]}]}
            """);

        ServiceAccountAttributeSubset.Validate(
            "sa-ok", new Dictionary<string, string> { ["clearance"] = "internal" }, registrar)
            .Should().BeEmpty("自分が読める区分は配れる");

        ServiceAccountAttributeSubset.Validate(
            "sa-ng", new Dictionary<string, string> { ["clearance"] = "confidential" }, registrar)
            .Should().ContainSingle().Which.Should().Contain("confidential");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 受け入れ基準 4（陽性・無制限）: 契約が「全件可」と定める形は無制限のまま
    // ─────────────────────────────────────────────────────────────────────────
    //
    // 契約 `AccessScopeResponse`:「AllowedFilters が空 かつ Granted=true は『条件無しで許可』」。
    // **ここまで deny へ倒すと、計画が許可と定めた形を実装が黙って狭める。**
    [Theory]
    // 未移行の発行者（Branches を運ばない）＋ フィルタ空。
    [InlineData("""{"userId":"tanaka","allowedFilters":[],"granted":true}""")]
    [InlineData("""{"userId":"tanaka","allowedFilters":[],"granted":true,"branches":[]}""")]
    // 文書条件を持たないポリシー＝分岐のフィルタが空（計画の「文書条件が無い場合は全件許可」）。
    [InlineData("""
        {"userId":"tanaka","allowedFilters":[],"granted":true,
         "branches":[{"name":"全員が全件読める","filters":[]}]}
        """)]
    public async Task 条件無しで許可されている登録者は無制限のままである(string scopeJson)
    {
        var registrar = await ResolveAsync(scopeJson);

        registrar.ClearanceUnrestricted.Should().BeTrue();
        ServiceAccountAttributeSubset.Validate(
            "sa-any", new Dictionary<string, string> { ["clearance"] = "restricted" }, registrar)
            .Should().BeEmpty();
    }

    // 後方互換（未移行の発行者）: `confidentiality` ただ 1 つの連言はそのまま読む。
    [Fact]
    public async Task 分岐を運ばない発行者でも単一キーの連言は読める()
    {
        var scope = await ResolveAsync("""
            {"userId":"tanaka",
             "allowedFilters":[{"key":"confidentiality","allowedValues":["public","internal"]}],
             "granted":true}
            """);

        scope.Clearance.Should().BeEquivalentTo(["public", "internal"]);
    }

    // 後方互換の陰性: 分岐が無く、`owner` が混ざっている union は**読まない**（deny 側）。
    [Fact]
    public async Task 分岐を運ばない発行者で他キーが混ざる_union_は読まない()
    {
        var scope = await ResolveAsync("""
            {"userId":"tanaka",
             "allowedFilters":[{"key":"owner","allowedValues":["${current_user}"]},
                               {"key":"confidentiality","allowedValues":["restricted"]}],
             "granted":true}
            """);

        scope.ClearanceUnrestricted.Should().BeFalse();
        scope.Clearance.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 受け入れ基準 5: `Granted=false` は空集合であり、「引けなかった」ではない
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task 許可ポリシーが無い登録者は空集合であって未解決ではない()
    {
        var scope = await ResolveAsync("""{"userId":"tanaka","allowedFilters":[],"granted":false}""");

        scope.Available.Should().BeTrue("引けている。**配れるものが無いだけである**");
        scope.ClearanceUnrestricted.Should().BeFalse();
        scope.Clearance.Should().BeEmpty();
    }

    // 縮退（陽性対照の対）: 認可サービスが落ちていれば `Unavailable`。**空集合と混ぜない。**
    [Fact]
    public async Task 認可スコープを引けなければ未解決になる()
    {
        var handler = new StubHandler()
            .Get("/authz/users", Directory())
            .Status("POST", "/authz/scope", HttpStatusCode.ServiceUnavailable);

        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, Registrar)], "test")),
            },
        };

        var scope = await new AuthorizationServiceRegistrarAttributes(
            new StubFactory(handler), accessor,
            NullLogger<AuthorizationServiceRegistrarAttributes>.Instance).ResolveAsync(Ct);

        scope.Available.Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 経路の固定: `/authz/scope` は **read** を解決する（write のスコープではない）
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task 認可スコープは_read_で解決する()
    {
        var handler = new StubHandler()
            .Get("/authz/users", Directory())
            .Post("/authz/scope", """{"userId":"tanaka","allowedFilters":[],"granted":true}""");

        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, Registrar)], "test")),
            },
        };

        await new AuthorizationServiceRegistrarAttributes(
            new StubFactory(handler), accessor,
            NullLogger<AuthorizationServiceRegistrarAttributes>.Instance).ResolveAsync(Ct);

        handler.Requests.Should().ContainSingle(r => r.Path == "/authz/scope")
            .Which.Body.Should().Contain("\"read\"");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IADR-0386 (#1243): 登録者のタグは畳まれない（集合として届く）
    // ─────────────────────────────────────────────────────────────────────────
    //
    // 🔴 **稼働で実測された欠陥**（#1185 の再測。`tags=sales,hr` の登録者へ `finance` を要求した
    // ところ「登録者が持つタグは 'sales' です」と返り、**`hr` が消えていた**）。上流の
    // `KeycloakIdentityAdminClient` が多値を先頭 1 値へ畳んでいたのが原因である。
    // ここでは**名簿がタグ集合を線上表現で運んできたとき、判定まで集合のまま届く**ことを固定する。
    //
    // 🔴 **変異試験**: 契約側 `UserAttributeEncoding.Split` を「分割しない」（値 1 つの集合を返す）
    // へ戻すと、下の陽性 2 本が落ちる。**陰性 1 本は緑のまま通る**（＝「常に配れる」実装ではない）。
    private const string OpenScope = """{"userId":"tanaka","allowedFilters":[],"granted":true}""";

    [Theory]
    // 正準形（Keycloak の多値配列を連結したもの）
    [InlineData("sales,hr")]
    // 人手入力の揺れ・順序違い。**どれも同じ集合である。**
    [InlineData("hr, sales")]
    [InlineData("sales hr")]
    public async Task 登録者のタグ集合は先頭一値へ畳まれない(string tags)
    {
        var registrar = await ResolveAsync(OpenScope, tags);

        registrar.Tags.Should().BeEquivalentTo(["sales", "hr"]);

        // 陽性: **2 つ目のタグを配れる**（従前はここが 400 になっていた）。
        ServiceAccountAttributeSubset.Validate(
            "sa-batch", new Dictionary<string, string> { ["tags"] = "hr" }, registrar)
            .Should().BeEmpty("登録者は hr を持っている");

        // 陽性: 両方まとめても配れる。
        ServiceAccountAttributeSubset.Validate(
            "sa-batch", new Dictionary<string, string> { ["tags"] = "sales,hr" }, registrar)
            .Should().BeEmpty();
    }

    // 🔴 **陰性対照（対で置く）。** 集合が広がったのであって、判定が緩んだのではない。
    [Fact]
    public async Task 登録者が持たないタグは配れない()
    {
        var registrar = await ResolveAsync(OpenScope, "sales,hr");

        ServiceAccountAttributeSubset.Validate(
            "sa-batch", new Dictionary<string, string> { ["tags"] = "sales,finance" }, registrar)
            .Should().ContainSingle().Which.Should()
            // **差集合だけを名指す** —— 外れていない `sales` を拒否理由の側へ混ぜない
            // （登録者が持つ集合の列挙としては現れる。それは理由ではなく手掛かりである）。
            .StartWith("tags の値 'finance' は割り当てられません")
            .And.Contain("登録者が持つタグは 'hr', 'sales' です");
    }

    // タグを 1 つも持たない登録者は 1 つも配れない（従来どおり。空集合は「引けなかった」ではない）。
    [Fact]
    public async Task タグを持たない登録者は何も配れない()
    {
        var registrar = await ResolveAsync(OpenScope);

        registrar.Tags.Should().BeEmpty();
        ServiceAccountAttributeSubset.Validate(
            "sa-batch", new Dictionary<string, string> { ["tags"] = "sales" }, registrar)
            .Should().ContainSingle().Which.Should().Contain("sales").And.Contain("ありません");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 受け入れ基準 6: 実行時の一律除外は本変更と**独立**であり、二重に掛かる
    // ─────────────────────────────────────────────────────────────────────────
    //
    // 本変更が効くのは**登録時の割当**だけである。実行経路の除外
    // （ADR-0034 決定 9 の `private-note` ＋ IADR-0373 の `project=ai-stock-trading`）は
    // 別の軸であり、**登録者が無制限でも外れない**。
    // 🔴 **緩い側の登録者で確かめる** —— 割当が通る条件でなお除外が効くことを見ないと、
    // 「割当で弾かれていただけ」を「除外が効いている」と読み違える。
    [Fact]
    public async Task 無制限の登録者が作った無人アカウントでも実行時の一律除外は外れない()
    {
        var registrar = await ResolveAsync("""{"userId":"tanaka","allowedFilters":[],"granted":true}""");

        // 割当は通る（登録者は条件無しで許可されている）。
        ServiceAccountAttributeSubset.Validate(
            "sa-batch", new Dictionary<string, string> { ["clearance"] = "restricted" }, registrar)
            .Should().BeEmpty();

        // それでも実行経路では個人資料と制限プロジェクトの文書が落ちる。
        var filtered = new ServiceAccountDocumentFilter(
            NullLogger<ServiceAccountDocumentFilter>.Instance).Apply(
            new McpSubject("sa-batch", "sa-batch", McpClientKind.ServiceAccount,
                new Dictionary<string, string> { ["clearance"] = "restricted" }),
            new McpToolResult(
                [
                    new McpToolDocument("doc-private", "個人メモ", new Dictionary<string, string>
                    {
                        ["doc_scope"] = "private-note",
                    }),
                    new McpToolDocument("doc-ast", "AST の文書", new Dictionary<string, string>
                    {
                        ["project"] = "ai-stock-trading",
                    }),
                    new McpToolDocument("doc-org", "組織文書", new Dictionary<string, string>
                    {
                        ["confidentiality"] = "restricted",
                    }),
                ],
                TotalCount: 3));

        // 陽性対照つき: **`restricted` の組織文書は残る**（全部落とす実装と区別する）。
        filtered.Documents.Should().ContainSingle().Which.DocumentId.Should().Be("doc-org");
        filtered.TotalCount.Should().Be(1);
    }

    private sealed record Recorded(string Method, string Path, string? Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses =
            new(StringComparer.Ordinal);

        public List<Recorded> Requests { get; } = [];

        public StubHandler Get(string path, string body) => Register("GET", path, HttpStatusCode.OK, body);
        public StubHandler Post(string path, string body) => Register("POST", path, HttpStatusCode.OK, body);
        public StubHandler Status(string method, string path, HttpStatusCode status)
            => Register(method, path, status, "");

        private StubHandler Register(string method, string path, HttpStatusCode status, string body)
        {
            _responses[$"{method} {path}"] = (status, body);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Recorded(request.Method.Method, path, body));

            if (!_responses.TryGetValue($"{request.Method.Method} {path}", out var response))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };

            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://authz.example.test/"),
        };
    }
}
