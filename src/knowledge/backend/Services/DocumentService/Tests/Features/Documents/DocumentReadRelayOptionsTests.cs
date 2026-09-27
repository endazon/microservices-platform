using System.Security.Claims;
using AwesomeAssertions;
using DocumentService.Features.Documents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocumentService.Tests.Features.Documents;

// FR-19, NFR-09, 計画 ADR-0086 決定 1, ADR-0119 決定 3, [[IADR-0476]] 追記 (#1628):
// 信頼する中継者の集合の**構成の束縛**と判定。AC-5（既定は `bff` だけ・構成は既定を置き換える・空白だけは誰も信じない）。
//
// 🔴 束縛は本番と同じ `Configure<T>(IConfiguration)` で通す —— .NET の配列の束縛は初期値に構成値を**追記する**ので、
//   「構成したら `bff` が外れる」は束縛を通さないと観測できない。
public class DocumentReadRelayOptionsTests
{
    private static DocumentReadRelayOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.Configure<DocumentReadRelayOptions>(config.GetSection(DocumentReadRelayOptions.SectionName));
        return services.BuildServiceProvider().GetRequiredService<IOptions<DocumentReadRelayOptions>>().Value;
    }

    private static ClaimsPrincipal ServiceAccount(string clientId, bool withAzp = true)
    {
        var claims = new List<Claim> { new("preferred_username", $"service-account-{clientId}") };
        if (withAzp) claims.Add(new Claim("azp", clientId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "preferred_username", ClaimTypes.Role));
    }

    // AC-5: 未構成なら `bff` だけ。
    [Fact]
    public void 未構成なら許可集合はbffだけ()
    {
        var options = Bind([]);

        options.EffectiveClients.Should().Equal("bff");
        options.TrustsUserContextFrom(ServiceAccount("bff")).Should().BeTrue();
        options.TrustsUserContextFrom(ServiceAccount("ai-stock-trading-llm-caller")).Should().BeFalse();
    }

    // AC-5: 🔴 構成は既定を**置き換える**（`bff` が残らない）。
    [Fact]
    public void 構成すると既定を置き換えbffは残らない()
    {
        var options = Bind(new() { ["DocumentRead:TrustedUserContextClients:0"] = " other-relay " });

        options.EffectiveClients.Should().Equal("other-relay");
        options.TrustsUserContextFrom(ServiceAccount("other-relay")).Should().BeTrue("前後空白は落とす");
        options.TrustsUserContextFrom(ServiceAccount("bff")).Should().BeFalse("構成は既定を置き換える");
    }

    // AC-5: 空白だけの構成は誰も信じない（fail-closed）。
    [Fact]
    public void 空白だけの構成は誰も信じない()
    {
        var options = Bind(new() { ["DocumentRead:TrustedUserContextClients:0"] = "  " });

        options.EffectiveClients.Should().BeEmpty();
        options.TrustsUserContextFrom(ServiceAccount("bff")).Should().BeFalse();
    }

    // 🔴 監査 F1: 照合は**全体一致**である。前方一致・後方一致・部分一致で `bff` を含む別の client を信じない
    // （`clientId.StartsWith(c)` の変異を落とす）。
    [Theory]
    [InlineData("bff-x")]
    [InlineData("bffx")]
    [InlineData("xbff")]
    public void bffを含むだけの別のclientは信じない(string clientId)
    {
        var options = Bind([]);

        options.TrustsUserContextFrom(ServiceAccount("bff")).Should().BeTrue("対照: bff そのものは信じる");
        options.TrustsUserContextFrom(ServiceAccount(clientId)).Should().BeFalse(clientId);
        options.TrustsUserContextFrom(ServiceAccount(clientId, withAzp: false)).Should()
            .BeFalse($"利用者名から復元した {clientId} も同じ");
    }

    // 🔴 監査 F1: クライアント識別は `azp` が正で、利用者名（`service-account-bff`）は `azp` が無いときの代わりに過ぎない。
    // 利用者名が bff の形でも `azp` が別なら信じない。
    [Fact]
    public void 利用者名がservice_account_bffでもazpが別なら信じない()
    {
        var options = Bind([]);
        var mismatched = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("preferred_username", "service-account-bff"), new Claim("azp", "retrieval-service")],
            "test", "preferred_username", ClaimTypes.Role));

        options.TrustsUserContextFrom(mismatched).Should().BeFalse();
    }

    // 監査 F2: 配列ではなく 1 つの値（カンマ区切り）は束縛されず既定の bff へ静かに戻る —— 起動時に止める。
    [Fact]
    public void 一つの値で構成すると起動時に例外になり_束縛だけなら既定へ戻ってしまう()
    {
        var scalar = new Dictionary<string, string?> { ["DocumentRead:TrustedUserContextClients"] = "other-relay,bff-2" };

        Bind(scalar).EffectiveClients.Should().Equal(["bff"], "前提: 1 つの値は配列へ束縛されず既定へ戻る（止める理由）");
        var act = () => DocumentReadRelayOptions.ThrowIfScalar(
            new ConfigurationBuilder().AddInMemoryCollection(scalar).Build());
        act.Should().Throw<InvalidOperationException>().WithMessage("*TrustedUserContextClients__0*");

        // 対照: 配列の形・未構成は通る。
        DocumentReadRelayOptions.ThrowIfScalar(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DocumentRead:TrustedUserContextClients:0"] = "bff" }).Build());
        DocumentReadRelayOptions.ThrowIfScalar(new ConfigurationBuilder().Build());
    }

    // #1658: 共有部品へ寄せても 1 つの値の例外の文言は従前と同じ（配列の書き方と既定の bff を示す）。
    [Fact]
    public void 一つの値の例外の文言は従前と同じ()
    {
        var act = () => DocumentReadRelayOptions.ThrowIfScalar(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DocumentRead:TrustedUserContextClients"] = "bff" }).Build());

        act.Should().Throw<InvalidOperationException>().WithMessage(
            "DocumentRead:TrustedUserContextClients は配列で構成すること（環境変数なら DocumentRead__TrustedUserContextClients__0=bff）。"
            + "1 つの値（カンマ区切りを含む）は束縛されず、既定の bff へ戻ってしまう。");
    }

    // #1658: 共有部品へ寄せても既定の解決・置き換え・空白の扱いは現行と同値（複数要素・前後空白・空要素・重複は落とさない）。
    [Fact]
    public void 複数要素の構成は前後空白を落とし空白だけの要素を捨てて順に並べる()
    {
        var options = Bind(new()
        {
            ["DocumentRead:TrustedUserContextClients:0"] = " relay-a ",
            ["DocumentRead:TrustedUserContextClients:1"] = "   ",
            ["DocumentRead:TrustedUserContextClients:2"] = "relay-b",
            ["DocumentRead:TrustedUserContextClients:3"] = "relay-a",
        });

        options.EffectiveClients.Should().Equal("relay-a", "relay-b", "relay-a");
        options.TrustsUserContextFrom(ServiceAccount("relay-b")).Should().BeTrue();
        options.TrustsUserContextFrom(ServiceAccount("bff")).Should().BeFalse("構成は既定を置き換える（足し合わせない）");
    }

    // 判定の形: 機械であること ∧ クライアント識別が序数一致で許可集合に在ること。
    [Fact]
    public void 判定は機械の主体のクライアント識別を序数一致で見る()
    {
        var options = Bind([]);

        options.TrustsUserContextFrom(ServiceAccount("bff", withAzp: false)).Should()
            .BeTrue("azp が無ければ service-account-<clientId> から復元する（MachinePrincipal.ClientIdOf）");
        options.TrustsUserContextFrom(ServiceAccount("BFF")).Should().BeFalse("大小文字を畳まない");

        var human = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("preferred_username", "carol"), new Claim("azp", "bff")], "test", "preferred_username", ClaimTypes.Role));
        options.TrustsUserContextFrom(human).Should().BeFalse("azp=bff の人のトークンは中継者ではない");

        options.TrustsUserContextFrom(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeFalse("未認証");
        options.TrustsUserContextFrom(null).Should().BeFalse();
    }
}
