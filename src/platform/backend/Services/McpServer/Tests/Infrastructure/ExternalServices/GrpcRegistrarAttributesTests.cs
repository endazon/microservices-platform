using AwesomeAssertions;
using Grpc.Core;
using McpServer.Domain;
using McpServer.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Contracts.Dtos;

namespace McpServer.Tests.Infrastructure.ExternalServices;

// FR-16, FR-05, UC-09, SC-12, NFR-09, NFR-16, ADR-0029, ADR-0062 決定 2・3, ADR-0075,
// [[IADR-0379]] 決定 4, [[IADR-0384]] 決定 1, [[IADR-0385]] 決定 2, [[IADR-0401]] 決定 2・4・5 (#1255):
// 登録者の割当可能属性の **gRPC 実装に固有の性質**を固定する。
//
// 🔴 **「読み方」そのものはここで測らない。** 読み方（`RegistrarScopeReading`）は
// `AuthorizationServiceRegistrarAttributesTests` の 19 件が**両輸送を通して**固定している
// （同クラスの `ResolveAsync` が REST と gRPC の答えの一致を表明する）。
// ここに置くのは、REST 実装には存在しない性質 —— **何を送るか**と**輸送の失敗の落とし先**である。
[Trait("TestKind", "Unit")]
public class GrpcRegistrarAttributesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Registrar = "tanaka";

    private static readonly Dictionary<string, string> RegistrarAttributes =
        new() { ["department"] = "engineering" };

    private const string OpenScopeJson = """{"userId":"tanaka","allowedFilters":[],"granted":true}""";

    private static Platform.Shared.Contracts.Grpc.Authz.V1.ResolveScopeResponse OpenScope() =>
        AuthorizationServiceRegistrarAttributesTests.ToProto(
            new AccessScopeResponse("tanaka", [], true));

    private static GrpcRegistrarAttributes Resolver(
        FakeUserDirectoryClient directory, FakeAuthzScopeClient scopes, string username = Registrar) =>
        new(FakeAuthzGrpc.Directory(directory), FakeAuthzGrpc.Scopes(scopes),
            AuthorizationServiceRegistrarAttributesTests.Accessor(username),
            NullLogger<GrpcRegistrarAttributes>.Instance);

    // 🔴 T-P2-08: **登録者は自分自身の名前でしか引かない。**
    // 引く名前は `HttpContext.User.Identity.Name`（`preferred_username`）であり、要求本文から
    // 取らない —— 取ると「他人の属性で登録できる」経路になる。
    // 名簿の rpc は 1 人ぶんしか答えないので、**送った名前がそのまま射程である。**
    [Fact]
    public async Task 登録者は自分自身の名前でしか名簿を引かない()
    {
        var directory = FakeUserDirectoryClient.Returning("someone.else", RegistrarAttributes);
        var scopes = FakeAuthzScopeClient.Returning(OpenScope());

        await Resolver(directory, scopes, username: "carol").ResolveAsync(Ct);

        directory.LastRequestedUsername.Should().Be("carol");
        directory.CallCount.Should().Be(1);
    }

    // 主体の識別子が空なら**後段を 1 度も呼ばない**（REST 実装と同じ短絡）。
    // 陽性対照は上の 1 本（名前があれば 1 回呼ぶ）。
    [Fact]
    public async Task 主体を特定できなければ後段を呼ばずに未解決になる()
    {
        var directory = FakeUserDirectoryClient.Returning(Registrar, RegistrarAttributes);
        var scopes = FakeAuthzScopeClient.Returning(OpenScope());

        var result = await Resolver(directory, scopes, username: "").ResolveAsync(Ct);

        result.Available.Should().BeFalse();
        directory.CallCount.Should().Be(0);
        scopes.CallCount.Should().Be(0);
    }

    // 🔴 「名簿に居ない」は `Unavailable` へ倒す（REST 実装の判断をそのまま写した）。
    // 配らない点では空集合と同じだが、ここで「あなたは何も持っていません」と断定すると、
    // 名簿と主体識別子の食い違いが属性の不足として沈黙する。
    [Fact]
    public async Task 名簿に登録者が居なければ未解決になる()
    {
        var result = await Resolver(
            FakeUserDirectoryClient.NotFound(),
            FakeAuthzScopeClient.Returning(OpenScope())).ResolveAsync(Ct);

        result.Available.Should().BeFalse();
    }

    // 縮退（名簿側）: 輸送の失敗はすべて `Unavailable`。**空集合と混ぜない。**
    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unavailable)]
    public async Task 名簿を引けなければ未解決になる(StatusCode status)
    {
        var result = await Resolver(
            FakeUserDirectoryClient.Failing(status),
            FakeAuthzScopeClient.Returning(OpenScope())).ResolveAsync(Ct);

        result.Available.Should().BeFalse();
    }

    // 🔴 [[IADR-0401]] 決定 5 の本体。**スコープを引けなかったら `Unavailable` である。**
    //
    // `AuthzScopeGrpcClient.ResolveScopeAsync`（畳む方）を使うと、輸送の失敗が `Granted=false` に
    // なり、`Available=true`・`Clearance` 空・**しかしタグは配れる**という REST 実装には無い
    // 挙動になる（緩む向き）。本実装は `TryResolveScopeAsync`（畳まない方）を使う。
    //
    // 陰性対照として**タグを持たせてある** —— 畳む実装へ戻すと `Available=true` かつ
    // `Tags = {sales, hr}` になり、この表明が落ちる。
    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unavailable)]
    public async Task 認可スコープを引けなければ未解決になる_タグも配らない(StatusCode status)
    {
        var attributes = new Dictionary<string, string>(RegistrarAttributes) { ["tags"] = "sales,hr" };

        var result = await Resolver(
            FakeUserDirectoryClient.Returning(Registrar, attributes),
            FakeAuthzScopeClient.Failing(status)).ResolveAsync(Ct);

        result.Available.Should().BeFalse("引けなかったのであって『配れるものが無い』のではない");
        result.Tags.Should().BeEmpty("縮退を Granted=false へ畳むと、ここでタグが配れてしまう");
    }

    // 経路の固定: 認可スコープは **read** で解決する（write のスコープではない）。
    // 本文の `user_id` は**名簿側の正準表記**である（大小文字が違っても名簿の綴りで引く）。
    [Fact]
    public async Task 認可スコープは_read_で名簿側の正準表記を使って解決する()
    {
        var directory = FakeUserDirectoryClient.Returning(Registrar, RegistrarAttributes);
        var scopes = FakeAuthzScopeClient.Returning(OpenScope());

        await Resolver(directory, scopes, username: "TANAKA").ResolveAsync(Ct);

        scopes.LastRequest.Should().NotBeNull();
        scopes.LastRequest!.Action.Should().Be("read");
        scopes.LastRequest.UserId.Should().Be(Registrar);
    }

    // 🔴 T-P2-09: 集合値のタグが gRPC の線上表現（カンマ連結。[[IADR-0385]] 決定 2）で往復し、
    // **`hr` を配れる**。#1185 / #1243 で実測された「先頭 1 値へ畳まれる」欠陥の gRPC 版の再現防止。
    [Fact]
    public async Task 集合値のタグは畳まれずに届き_後ろの値も配れる()
    {
        var attributes = new Dictionary<string, string>(RegistrarAttributes) { ["tags"] = "sales,hr" };

        var result = await Resolver(
            FakeUserDirectoryClient.Returning(Registrar, attributes),
            FakeAuthzScopeClient.Returning(OpenScope())).ResolveAsync(Ct);

        result.Available.Should().BeTrue();
        result.Tags.Should().BeEquivalentTo(["sales", "hr"]);

        // 後段の判定まで通して見る（陽性対照つき）。
        ServiceAccountAttributeSubset.Validate(
            "sa-1", new Dictionary<string, string> { ["tags"] = "hr" }, result)
            .Should().BeEmpty();
        ServiceAccountAttributeSubset.Validate(
            "sa-2", new Dictionary<string, string> { ["tags"] = "finance" }, result)
            .Should().ContainSingle("持っていないタグは配れない（陰性対照）");
    }

    // 陽性対照の駄目押し: 上の偽クライアントの組み立てが、REST 実装と同じ答えを返すこと。
    // （`AuthorizationServiceRegistrarAttributesTests` の 19 件が同じ表明を毎回行うが、
    //   ここでも 1 本だけ明示的に置いて「器が壊れている」と「実装が違う」を分ける。）
    [Fact]
    public async Task 開いたスコープでは制約なしとして読める()
    {
        var result = await Resolver(
            FakeUserDirectoryClient.Returning(Registrar, RegistrarAttributes),
            FakeAuthzScopeClient.Returning(OpenScope())).ResolveAsync(Ct);

        result.Available.Should().BeTrue();
        result.ClearanceUnrestricted.Should().BeTrue();
        OpenScopeJson.Should().Contain("\"granted\":true", "入力の形を取り違えていない");
    }
}
