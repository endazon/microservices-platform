using AwesomeAssertions;
using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using System.Text.Json;
using Platform.Shared.Contracts.Dtos;
using Pb = Platform.Shared.Contracts.Grpc.Authz.V1;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// FR-16, FR-05, FR-09, UC-09, SC-12, ADR-0062 決定 2・3, ADR-0036 D-01・D-02, IADR-0384 (#1242):
// **本物の解決器**に対して AuthorizationService の応答をスタブし、認可スコープの読み方を固定する。
// ［2026-10-10 / #1255・[[IADR-0533]]］REST の解決器（`AuthorizationServiceRegistrarAttributes`）は撤去した。
// 従前は REST と gRPC の両実装を通して答えの一致を表明していたが、いまは gRPC の解決器（`GrpcRegistrarAttributes`）だけを通す。
// 読み方（`RegistrarScopeReading`）は 1 つのままであり、本クラスの各試験はその読み方を固定する。
// REST にしか無かった枝（2xx の空本文・非 2xx の WARN）の試験は撤去した（gRPC の失敗の枝は `GrpcRegistrarAttributesTests` が持つ）。
//
// 🔴 **本クラスが無かったことが #1242 の原因である。** 従前は `StubRegistrarAttributeResolver`
// （ヘッダで集合を注入する）経由の経路テストしか無く、**「スコープをどう読むか」は 1 本も
// 試験されていなかった**。判定の入力を作る側が試験されないと、fail-open は緑のまま通る。
//
// ここで固定するのは**読み方**である。疎通（実 AuthorizationService への到達）は測らない。
[Trait("TestKind", "Unit")]
public class RegistrarScopeReadingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Registrar = "tanaka";

    // 名簿の 1 行（登録者の属性）＋ スコープ JSON を east-west gRPC の偽クライアントへ載せる。
    private static Task<RegistrarAssignableAttributes> ResolveAsync(string scopeJson, string? tags = null)
        => ResolveOverGrpcAsync(scopeJson, tags);

    // 同じ入力（名簿の 1 行 ＋ スコープ JSON）を east-west gRPC の偽クライアントへ載せ替える。
    private static async Task<RegistrarAssignableAttributes> ResolveOverGrpcAsync(
        string scopeJson, string? tags)
    {
        var attributes = new Dictionary<string, string> { ["department"] = "engineering" };
        if (tags is not null) attributes["tags"] = tags;

        var scope = JsonSerializer.Deserialize<AccessScopeResponse>(
            scopeJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var directory = FakeUserDirectoryClient.Returning(Registrar, attributes);
        var scopes = FakeAuthzScopeClient.Returning(ToProto(scope));

        return await new GrpcRegistrarAttributes(
            FakeAuthzGrpc.Directory(directory), FakeAuthzGrpc.Scopes(scopes), Accessor(),
            NullLogger<GrpcRegistrarAttributes>.Instance).ResolveAsync(Ct);
    }

    // 契約 DTO → proto（呼び出し先 `AuthzScopeGrpcService` が行う写しと同じ形）。
    internal static Pb.ResolveScopeResponse ToProto(AccessScopeResponse scope)
    {
        var proto = new Pb.ResolveScopeResponse { UserId = scope.UserId, Granted = scope.Granted };
        proto.AllowedFilters.AddRange(scope.AllowedFilters.Select(ToProto));
        if (scope.Branches is { Count: > 0 })
            proto.Branches.AddRange(scope.Branches.Select(b =>
            {
                var branch = new Pb.AccessScopeBranch { Name = b.Name };
                branch.Filters.AddRange(b.Filters.Select(ToProto));
                return branch;
            }));
        return proto;
    }

    private static Pb.AttributeFilter ToProto(AttributeFilter f)
    {
        var proto = new Pb.AttributeFilter { Key = f.Key };
        proto.AllowedValues.AddRange(f.AllowedValues);
        return proto;
    }

    // 登録者の主体（`preferred_username`）。両実装が**同じ 1 つ**から名前を取る。
    internal static HttpContextAccessor Accessor(string username = Registrar) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, username)], "test")),
        },
    };

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

    // ─────────────────────────────────────────────────────────────────────────
    // 🔴 ADR-0062 実測 9 の契機の到来（#1664 / 計画 ADR-0121 決定 1）
    // ─────────────────────────────────────────────────────────────────────────
    //
    // ADR-0062 は #1242 の顕在化の契機を「**所有者の read 条件が seed に入った時点**」と記録した。
    // #1664 で seed に入ったので、**seed が実際に作る応答**を入力にして再顕在化が無いことを固定する。
    // 入力は認可サービスの試験（`OwnerReadPolicySeedTests`）が実物の応答と突き合わせている期待値のファイルであり、
    // 上の手組みの JSON（形の主張）とは別の担保である。

    // T-34: 属性を持たない登録者（所有者・共有先の分岐だけ）は、区分を 1 つも配れず、無制限にもならない。
    [Fact]
    public async Task Seedの構成で属性を持たない登録者は機密区分を配れない()
    {
        var registrar = await ResolveAsync(SeedScopeJson("alice"));

        registrar.Available.Should().BeTrue();
        registrar.ClearanceUnrestricted.Should().BeFalse("所有者・共有先の分岐は機密区分の軸の許可ではない");
        registrar.Clearance.Should().BeEmpty();
        ServiceAccountAttributeSubset.Validate(
            "sa-escalation", new Dictionary<string, string> { ["clearance"] = "restricted" }, registrar)
            .Should().ContainSingle();
    }

    // T-35: `clearance=internal` の登録者は階段の分だけ（public・internal）を配れ、所有者の分岐で広がらない。
    [Fact]
    public async Task Seedの構成で階段の登録者は自分の区分だけを配れる()
    {
        var registrar = await ResolveAsync(SeedScopeJson("bob"));

        registrar.ClearanceUnrestricted.Should().BeFalse();
        registrar.Clearance.Should().BeEquivalentTo(["public", "internal"]);
        ServiceAccountAttributeSubset.Validate(
            "sa-ok", new Dictionary<string, string> { ["clearance"] = "internal" }, registrar)
            .Should().BeEmpty("陽性対照: 自分が読める区分は配れる");
        ServiceAccountAttributeSubset.Validate(
            "sa-escalation", new Dictionary<string, string> { ["clearance"] = "confidential" }, registrar)
            .Should().ContainSingle();
    }

    // seed を入れた認可サービスの応答の期待値（正は AuthorizationService/Tests/Fixtures/owner-read-seed-scopes.json）。
    private static string SeedScopeJson(string userId)
    {
        const string relative = "src/platform/backend/Services/AuthorizationService/Tests/Fixtures/owner-read-seed-scopes.json";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["subjects"]!.AsArray()
                .Single(s => (string)s!["userId"]! == userId)!["scope"]!.ToJsonString();
        }
        throw new FileNotFoundException($"リポジトリの {relative} が見つからない（走査の起点: {AppContext.BaseDirectory}）");
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
        var directory = FakeUserDirectoryClient.Returning(Registrar, new Dictionary<string, string> { ["department"] = "engineering" });
        var scopes = FakeAuthzScopeClient.Failing(Grpc.Core.StatusCode.Unavailable);

        var scope = await new GrpcRegistrarAttributes(
            FakeAuthzGrpc.Directory(directory), FakeAuthzGrpc.Scopes(scopes), Accessor(),
            NullLogger<GrpcRegistrarAttributes>.Instance).ResolveAsync(Ct);

        scope.Available.Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IADR-0385 (#1243): 登録者のタグは畳まれない（集合として届く）
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
}
