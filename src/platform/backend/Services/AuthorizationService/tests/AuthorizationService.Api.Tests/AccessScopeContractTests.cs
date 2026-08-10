using FluentAssertions;
using Platform.Shared.Contracts.Dtos;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuthorizationService.Api.Tests;

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
            new AccessScopeRequest("contract-probe", new Dictionary<string, string>()));

        res.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();

        body.Should().ContainKey("granted");
        // granted は真偽値である（値そのものは共有 DB のポリシー状態に依存するため主張しない）。
        body["granted"]!.GetValueKind().Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }
}
