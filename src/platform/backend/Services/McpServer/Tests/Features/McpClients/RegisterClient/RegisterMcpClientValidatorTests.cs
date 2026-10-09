using AwesomeAssertions;
using McpServer.Features.McpClients;
using McpServer.Features.McpClients.RegisterClient;

namespace McpServer.Tests.Features.McpClients.RegisterClient;

// FR-16, UC-09 基本フロー 1, SC-12, 計画 ADR-0024 / ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / [[IADR-0398]] 決定 1 (b)・5・9: `RegisterMcpClientValidator` の単体試験。
//
// 器（HTTP）を通さずに **宣言順・件数・メッセージ・鍵の所在**を固定する。
// 応答としての鍵とメッセージは `McpValidationProblemContractTests` が端点越しに固定しており、
// ここは**その上流**（検証器そのもの）を見る。
public class RegisterMcpClientValidatorTests
{
    private static readonly RegisterMcpClientValidator Validator = new();

    // ［#1844］有人の既定の要求は正しいリダイレクト URI を 1 件持つ（有人は必須になった。計画 ADR-0134 決定 1）。
    private static readonly List<string> ValidRedirect = ["https://agent.example.test/callback"];

    private static RegisterMcpClientRequest Request(
        string clientId = "agent-ok", string kind = "interactive", string? egressTier = null,
        List<string>? redirectUris = null, bool omitRedirectUris = false)
        => new(clientId, "表示名", kind, null, egressTier,
            omitRedirectUris ? null
            : redirectUris ?? (kind.Trim().Equals("interactive", StringComparison.OrdinalIgnoreCase) ? ValidRedirect : null));

    // 陽性対照: 全規則を満たす要求は通る。これが無いと「常に落ちる検証器」と区別できない。
    [Fact]
    public void ValidRequest_Passes()
    {
        var result = Validator.Validate(Request(egressTier: "self-hosted"));

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // M1: メッセージは**定数とリテラルの両方**へ当てる（定数だけを書き換える退行を止める）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankClientId_FailsWithOriginalMessage(string clientId)
    {
        var result = Validator.Validate(Request(clientId: clientId));

        result.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(RegisterMcpClientValidator.ClientIdRequiredMessage);
        RegisterMcpClientValidator.ClientIdRequiredMessage.Should().Be("clientId は必須です。");
    }

    // M2: 補間を含むので定数ではなく関数である。**関数とリテラルの両方**へ当てる。
    [Fact]
    public void InvalidKind_FailsWithOriginalMessage()
    {
        var result = Validator.Validate(Request(kind: "robot"));

        result.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(RegisterMcpClientValidator.KindInvalidMessage("robot"));
        RegisterMcpClientValidator.KindInvalidMessage("robot").Should()
            .Be("kind の値 'robot' は不正です（interactive / service-account）。");
    }

    // M3: 同上。
    [Fact]
    public void InvalidEgressTier_FailsWithOriginalMessage()
    {
        var result = Validator.Validate(Request(egressTier: "moon"));

        result.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(RegisterMcpClientValidator.EgressTierInvalidMessage("moon"));
        RegisterMcpClientValidator.EgressTierInvalidMessage("moon").Should()
            .Be("egressTier の値 'moon' は不正です。");
    }

    // 🔴 O 軸・C 軸: 3 つとも不正なら失敗は **3 件**あり、**先頭は移送前の最初のガード節**である。
    // 端点が載せるのは `Errors[0]` だけなので、宣言順を入れ替えると応答が変わる。
    [Fact]
    public void AllThreeInvalid_ReportsClientIdFirst()
    {
        var result = Validator.Validate(Request(clientId: " ", kind: "robot", egressTier: "moon"));

        result.Errors.Should().HaveCount(3);
        result.Errors[0].ErrorMessage.Should().Be(RegisterMcpClientValidator.ClientIdRequiredMessage);
        result.Errors[1].ErrorMessage.Should().Be(RegisterMcpClientValidator.KindInvalidMessage("robot"));
        result.Errors[2].ErrorMessage.Should().Be(RegisterMcpClientValidator.EgressTierInvalidMessage("moon"));
    }

    // 🔴 K 軸: **鍵は sink（`McpClientEndpoints.Problem` の `request`）が持ち、検証器は持たない**
    // （[[IADR-0398]] 決定 1 (b)）。したがって `PropertyName` は推論名のままであり、
    // **応答の鍵と一致しない**。ここに `OverridePropertyName` を足すと鍵の正が 2 つになるので落とす。
    [Fact]
    public void Validator_DoesNotOwnTheResponseKey()
    {
        var result = Validator.Validate(Request(clientId: " ", kind: "robot", egressTier: "moon"));

        result.Errors.Select(e => e.PropertyName).Should().Equal(["ClientId", "Kind", "EgressTier"]);
        result.Errors.Select(e => e.PropertyName).Should().NotContain("request");
    }

    // 🔴 G 軸: `TryParseKind` は `Trim().ToLowerInvariant()` してから比較する。
    // 検証器が自前の集合比較を持つと、ここで割れる（**同じ関数を共有している**ことの試験）。
    [Theory]
    [InlineData("interactive")]
    [InlineData("service-account")]
    [InlineData(" Service-Account ")]
    [InlineData("INTERACTIVE")]
    public void KindWithCaseAndSpaces_Passes(string kind)
        => Validator.Validate(Request(kind: kind)).IsValid.Should().BeTrue();

    // 🔴 G 軸: `egressTier` の**未指定・空白は有効**（既定は最も低い保護水準）。
    // `NotEmpty()` へ置き換えるとここで落ちる。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Self-Hosted ")]
    public void MissingOrLooselyWrittenEgressTier_Passes(string? egressTier)
        => Validator.Validate(Request(egressTier: egressTier)).IsValid.Should().BeTrue();

    // ── ［#1844］リダイレクト URI（計画 ADR-0134 決定 1・SC-12 の入力表）────────────────────────

    // 陽性対照: https・port を明示したループバック（v4・v6。path・クエリの有無を問わない）は通る。
    [Theory]
    [InlineData("https://agent.example.test/callback")]
    [InlineData("https://agent.example.test:8443/cb?x=1")]
    [InlineData("https://agent.example.test/cb")]
    [InlineData("http://127.0.0.1:53123/callback")]
    [InlineData("http://127.0.0.1:53123")]
    [InlineData("http://127.0.0.1:53123?x=1")]
    [InlineData("http://127.0.0.1:80/cb")]
    [InlineData("http://[::1]:53123/callback")]
    [InlineData("http://[::1]:53123")]
    public void InteractiveWithAllowedRedirectUri_Passes(string uri)
        => Validator.Validate(Request(redirectUris: [uri])).IsValid.Should().BeTrue();

    // 🔴 否定形: ワイルドカード・非ループバックの http・localhost・ループバックに見える別ホスト・フラグメント・利用者情報・相対・
    // 他のスキーム・前後の空白は、いずれも 1 件の 400。
    [Theory]
    [InlineData("https://agent.example.test/*", "ワイルドカード")]
    [InlineData("https://*.example.test/cb", "ワイルドカード")]
    [InlineData("*", "ワイルドカード")]
    [InlineData("http://agent.example.test/callback", "https か")]
    [InlineData("http://localhost:8080/callback", "https か")]
    [InlineData("http://127.0.0.2/callback", "https か")]
    [InlineData("http://127.0.0.1.evil.example/callback", "https か")]
    [InlineData("http://127.1/callback", "https か")]
    [InlineData("https://agent.example.test/cb#frag", "フラグメント")]
    [InlineData("https://user@agent.example.test/cb", "利用者情報")]
    [InlineData("/callback", "絶対 URI")]
    [InlineData("myapp://callback", "https か")]
    [InlineData(" https://agent.example.test/cb", "空白")]
    [InlineData("", "空の値")]
    public void InteractiveWithForbiddenRedirectUri_FailsWithReason(string uri, string reason)
    {
        var result = Validator.Validate(Request(redirectUris: [uri]));

        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain(reason);
    }

    // 🔴 ［2026-10-09 / #1844］CVE-2024-8883: port なしのループバックは 400。Keycloak 24 は port なしで登録された
    // `http://127.0.0.1/cb` に `http://127.0.0.1:49152@evil.example/cb` を一致させ、認可コードを外へ送る。
    // `[::1]` も同じ規則に揃える。path の有無・クエリつき・`:` だけで数字が無い形も port の明示とは見ない。
    [Theory]
    [InlineData("http://127.0.0.1/callback")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1?x=1")]
    [InlineData("http://127.0.0.1:/callback")]
    [InlineData("http://[::1]/callback")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::1]")]
    [InlineData("http://[::1]:/callback")]
    public void InteractiveWithPortlessLoopbackRedirectUri_FailsWithReason(string uri)
    {
        var result = Validator.Validate(Request(redirectUris: [uri]));

        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("port を明示");
    }

    // 🔴 port を明示しても、利用者情報で host を偽る形は従来どおり 400（Keycloak 24 の横取りの形そのもの）。
    [Theory]
    [InlineData("http://127.0.0.1:49152@evil.example/cb")]
    [InlineData("http://[::1]:49152@evil.example/cb")]
    public void LoopbackLookalikeWithUserInfo_Fails(string uri)
        => Validator.Validate(Request(redirectUris: [uri])).IsValid.Should().BeFalse();

    [Fact]
    public void InteractiveWithoutRedirectUris_Fails()
    {
        Validator.Validate(Request(omitRedirectUris: true)).Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("有人（interactive）にはリダイレクト URI が 1 件以上必要です。");
        Validator.Validate(Request(redirectUris: [])).Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("1 件以上");
    }

    [Fact]
    public void InteractiveWithDuplicateOrTooManyRedirectUris_Fails()
    {
        Validator.Validate(Request(redirectUris: ["https://a.example.test/cb", "https://a.example.test/cb"]))
            .Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("重複");
        var eleven = Enumerable.Range(0, 11).Select(i => $"https://a.example.test/cb{i}").ToList();
        Validator.Validate(Request(redirectUris: eleven))
            .Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("10 件以下");
        Validator.Validate(Request(redirectUris: eleven[..10])).IsValid.Should().BeTrue("上限ちょうどは通る（境界）");
    }

    // 🔴 無人へ渡したら（空配列を含めて）400。受け取って黙って捨てない。
    [Fact]
    public void ServiceAccountWithRedirectUris_Fails()
    {
        Validator.Validate(Request(kind: "service-account", redirectUris: ["https://a.example.test/cb"]))
            .Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(RegisterMcpClientValidator.RedirectUrisNotAllowedMessage);
        Validator.Validate(Request(kind: "service-account", redirectUris: []))
            .Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(RegisterMcpClientValidator.RedirectUrisNotAllowedMessage);
    }

    // 🔴 O 軸: リダイレクト URI の規則は `kind` の後・`egressTier` の前（種別が決まらなければ判定しない）。
    [Fact]
    public void RedirectUriRule_IsBetweenKindAndEgressTier()
    {
        var result = Validator.Validate(Request(clientId: " ", redirectUris: ["http://example.test/cb"], egressTier: "moon"));

        result.Errors.Select(e => e.PropertyName).Should().Equal(["ClientId", "RedirectUris", "EgressTier"]);
        Validator.Validate(Request(kind: "robot", redirectUris: ["http://example.test/cb"]))
            .Errors.Should().ContainSingle("種別が決まらなければリダイレクト URI は判定しない");
    }
}
