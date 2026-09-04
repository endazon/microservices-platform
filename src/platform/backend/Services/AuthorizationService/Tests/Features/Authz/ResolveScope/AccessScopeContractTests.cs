using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuthorizationService.Tests.Features.Authz.ResolveScope;

// FR-05, ADR-0004, #525: スコープ解決の応答が「全件遮断」と「全件許可」を**契約の上で**
// 区別できることを固定する。
//
// AbacEvaluatorTests（T-01 / T-04）は C# オブジェクトの `Granted` を見ており、
// **シリアライズを通っていない**。#525 が言っているのは「契約から区別できない」ことなので、
// ここでは**本文（JSON）を直接読む**。
//
// なぜ `POST /authz/scope` の応答値を端点越しに固定しないか:
//   TestWebApplicationFactory は InMemory DB を**固定名 `AuthzTest`** で張っており、
//   プロセス内の全テストで共有される。既存テストは**利用者条件が空のポリシー**を複数作っており
//   （`AbacEvaluator.MatchesUserConditions` は条件が空なら全利用者にマッチする）、
//   `granted=false` を端点越しに固定するとテストの実行順に依存して壊れる。
//   よって「値の対応」は決定的なシリアライズで、「本文に載っていること」は端点で固定する。
[Trait("TestKind", "Integration")]
public class AccessScopeContractTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    // 端点（minimal API）と同じ既定（camelCase）で直列化する。
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static JsonObject Serialize(AccessScopeResponse scope) =>
        JsonNode.Parse(JsonSerializer.Serialize(scope, WebJson))!.AsObject();

    // FR-05, T-17（#525): 全件遮断（マッチするポリシーが 1 件も無い）が本文で表せる。
    [Fact]
    public void Serialize_NotGranted_CarriesGrantedFalseOnTheWire()
    {
        var body = Serialize(new AccessScopeResponse("u1", [], Granted: false));

        body["granted"]!.GetValue<bool>().Should().BeFalse();
        body["allowedFilters"]!.AsArray().Should().BeEmpty(
            "全件遮断でもフィルタは空である——だから granted が要る");
    }

    // FR-05, T-18（#525): 条件なしの全件許可が本文で表せ、**全件遮断と区別できる**。
    [Fact]
    public void Serialize_GrantedWithoutFilters_IsDistinguishableFromDenyAll()
    {
        var denyAll = Serialize(new AccessScopeResponse("u1", [], Granted: false));
        var allowAll = Serialize(new AccessScopeResponse("u1", [], Granted: true));

        allowAll["granted"]!.GetValue<bool>().Should().BeTrue();

        // ★ 本件の核心。`allowedFilters` は両方とも空なので、それだけでは同じ応答になる。
        allowAll["allowedFilters"]!.ToJsonString()
            .Should().Be(denyAll["allowedFilters"]!.ToJsonString());
        allowAll.ToJsonString().Should().NotBe(denyAll.ToJsonString(),
            "granted が無ければ deny-by-default と全件許可が同一の本文になる");
    }

    // FR-05, T-17 / T-18（#525): `POST /authz/scope` の本文に granted が実際に載る。
    // **値は主張しない**——共有 InMemory DB のポリシー状態に依存するため（上記コメント）。
    [Fact]
    public async Task ResolveScopeEndpoint_ResponseBodyContainsGranted()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest("contract-probe", new Dictionary<string, string>()), TestContext.Current.CancellationToken);

        res.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();

        body.Should().ContainKey("granted");
        // granted は真偽値である（値そのものは共有 DB のポリシー状態に依存するため主張しない）。
        body["granted"]!.GetValueKind().Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }

    // ---- FR-19, ADR-0036, ADR-0046 D-06 部品 3, IADR-0253 段 1: 名前つき分岐 ----------------
    //
    // 🔴 **段 1 の完了条件は「Branches が常に null であること」である。**
    // 本段は契約へ型を足すだけで、評価器（段 2）も消費側（段 3）も変えていない。
    // したがって**挙動は 1 ビットも変わらない**——下のテストはその「変わらなさ」を固定する。

    // 既定は null。既存の 3 引数呼び出しがそのまま通る（既定値付き追加＝非破壊）。
    [Fact]
    public void Branches_DefaultsToNull_SoExistingCallSitesAreUnchanged()
    {
        var scope = new AccessScopeResponse("u1", [], Granted: true);

        scope.Branches.Should().BeNull(
            "段 1 では分岐を組み立てる生産者が 1 つも無い（評価器の対応は段 2）");
    }

    // 既定の応答は、分岐の導入前と同じ意味を運ぶ（後方互換）。
    // `allowedFilters` と `granted` の載り方が変わっていないことを固定する。
    [Fact]
    public void Serialize_WithoutBranches_KeepsAllowedFiltersAndGrantedUnchanged()
    {
        var body = Serialize(new AccessScopeResponse(
            "u1", [new AttributeFilter("confidentiality", ["internal"])], Granted: true));

        body["granted"]!.GetValue<bool>().Should().BeTrue();
        body["allowedFilters"]!.AsArray().Should().HaveCount(1);
        body["allowedFilters"]![0]!["key"]!.GetValue<string>().Should().Be("confidentiality");
    }

    // 分岐が表現できる: 分岐内は AND（フィルタの並び）、分岐どうしは OR（要素の並び）。
    // **意味論はここで固定するが、この形を作る生産者は段 2 まで存在しない。**
    [Fact]
    public void Branches_CanCarryDisjunctionOfConjunctions()
    {
        var scope = new AccessScopeResponse("u1", [], Granted: true, Branches:
        [
            new AccessScopeBranch("attribute", [new AttributeFilter("confidentiality", ["internal"])]),
            new AccessScopeBranch("owner", [new AttributeFilter("owner", ["u1"])]),
        ]);

        scope.Branches.Should().HaveCount(2, "read 規則は 3 節の選言であり、単一の連言では表せない");
        scope.Branches![0].Name.Should().Be("attribute");
        scope.Branches[1].Name.Should().Be("owner");

        var body = Serialize(scope);
        body["branches"]!.AsArray().Should().HaveCount(2);
        body["branches"]![1]!["name"]!.GetValue<string>().Should().Be("owner",
            "分岐に名前が要る——計画が『どの分岐で検証したかを必ず添えること』と定めている");
    }

    // 🔴 `AllowedFilters` は分岐の**積**に相当し、`Branches` は分岐の**和**である。
    // 未移行のサービスは `AllowedFilters` しか読まないため、**余分に見せることは構造上あり得ない**
    // （IADR-0253 決定 2）。ここでは「両方が本文に載り、別のキーとして区別できる」ことを固定する。
    [Fact]
    public void Serialize_WithBranches_KeepsAllowedFiltersSeparatelyForUnmigratedConsumers()
    {
        var body = Serialize(new AccessScopeResponse(
            "u1",
            [new AttributeFilter("confidentiality", ["internal"])],
            Granted: true,
            Branches: [new AccessScopeBranch("owner", [new AttributeFilter("owner", ["u1"])])]));

        // 未移行の消費側が読む面は従来のまま。
        body["allowedFilters"]!.AsArray().Should().HaveCount(1);
        body["allowedFilters"]![0]!["key"]!.GetValue<string>().Should().Be("confidentiality");
        // 移行済みの消費側が読む面は別キーで並存する。
        body["branches"]!.AsArray().Should().HaveCount(1);
        body["branches"]![0]!["filters"]![0]!["key"]!.GetValue<string>().Should().Be("owner");
    }

    // ---- FR-21, ADR-0036 D-07, IADR-0253 決定 5（2026-08-23 改定 / #989）段 5: Action -----------

    // 旧発行者（action プロパティを知らないクライアント）の本文は既定 read として読める
    // （既定値付き末尾追加＝非破壊、の「非破壊」が実際に効いていることの固定）。
    [Fact]
    public void Deserialize_RequestWithoutAction_DefaultsToRead()
    {
        var req = JsonSerializer.Deserialize<AccessScopeRequest>(
            """{"userId":"u1","userAttributes":{}}""", WebJson);

        req!.Action.Should().Be("read",
            "既定が read でなければ、未改修の全呼び出し元のスコープ解決が壊れる");
    }

    // 段 5 の陽性対照: action を明示した本文はその値で読める。
    [Fact]
    public void Deserialize_RequestWithAction_CarriesIt()
    {
        var req = JsonSerializer.Deserialize<AccessScopeRequest>(
            """{"userId":"u1","userAttributes":{},"action":"write"}""", WebJson);

        req!.Action.Should().Be("write");
    }

    // 🔴 否定形: 値域外の action は 400 で拒否される（黙って空スコープへ写さない）。
    // 400 は全消費側で Granted=false へ縮退する——deny 側の失敗であり、緩む向きではない。
    [Fact]
    public async Task ResolveScopeEndpoint_UnknownAction_Returns400()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest("contract-probe", new Dictionary<string, string>(), "delete"),
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    // 陽性対照（上の否定形と対）: 有効な write は 200 で解決される。
    // **値は主張しない**——共有 InMemory DB のポリシー状態に依存するため（クラス冒頭コメント）。
    [Fact]
    public async Task ResolveScopeEndpoint_WriteAction_IsAccepted()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest("contract-probe", new Dictionary<string, string>(), "write"),
            TestContext.Current.CancellationToken);

        res.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken))!.AsObject();
        body.Should().ContainKey("granted");
    }

    // 端点越しに action が評価器へ届くこと（ハードコード解消の証拠）。
    //
    // **他テストのポリシーが共有 DB に混在しても壊れない形にしてある**——主張は
    // 「write スコープには自分の write ポリシーの分岐が**含まれる**」「read スコープには
    // **含まれない**」だけであり、他ポリシーの有無・順序に依存しない。
    // 端点が Read をハードコードへ戻ると、含まれるはずの分岐が消えて前者が落ちる。
    [Fact]
    public async Task ResolveScopeEndpoint_RoutesActionToEvaluator()
    {
        var client = factory.CreateClient();
        var policyName = $"write-probe-{Guid.NewGuid():N}";

        // 管理 API で write ポリシーを登録する（値域拡張が保存経路でも通ることの検証を兼ねる）。
        var created = await client.PostAsJsonAsync("/authz/policies", new
        {
            name = policyName,
            action = "write",
            userConditions = new Dictionary<string, string[]> { ["probe-989"] = ["yes"] },
            documentConditions = new Dictionary<string, string[]> { ["owner"] = ["${current_user}"] },
        }, TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();

        var attrs = new Dictionary<string, string> { ["probe-989"] = "yes" };

        // 陽性対照: write スコープに自分の write ポリシーの分岐が含まれ、束縛済みである。
        var writeRes = await client.PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest("alice-989", attrs, "write"), TestContext.Current.CancellationToken);
        writeRes.EnsureSuccessStatusCode();
        var writeBody = JsonNode.Parse(await writeRes.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken))!.AsObject();
        var writeBranches = writeBody["branches"]!.AsArray();
        var mine = writeBranches.Single(b => b!["name"]!.GetValue<string>() == policyName);
        mine!["filters"]![0]!["allowedValues"]!.AsArray().Single()!.GetValue<string>()
            .Should().Be("alice-989", "束縛は分岐の中で解決されて端点から返る");

        // 否定形（対）: read スコープには write ポリシーの分岐が混ざらない。
        var readRes = await client.PostAsJsonAsync("/authz/scope",
            new AccessScopeRequest("alice-989", attrs, "read"), TestContext.Current.CancellationToken);
        readRes.EnsureSuccessStatusCode();
        var readBody = JsonNode.Parse(await readRes.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken))!.AsObject();
        readBody["branches"]!.AsArray()
            .Should().NotContain(b => b!["name"]!.GetValue<string>() == policyName,
                "write ポリシーが read へ漏れると閲覧範囲が書き込みポリシーで変わってしまう");
    }
}
