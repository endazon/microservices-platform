using System.Security.Claims;
using System.Text;
using AwesomeAssertions;
using DataSourceService.Domain;
using DataSourceService.Features.DataSources.Create;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DataSourceService.Tests.Domain;

// FR-05, UC-04, SC-06, IADR-0468 (#754): 登録者の部門グループの所属から部門コードを導く規則。
//
// 裁定（利用者 2026-09-26）: 部門コードの値域は Keycloak realm の `department` グループであり、
// データソースの部門は**登録した利用者の部門グループの所属**から導く。
// 🔴 **ちょうど 1 つのときだけ導く** —— 計画 09_datasource-connectors「安全側は『解決しない』」。
[Trait("TestKind", "Unit")]
public class RegistrantDepartmentTests
{
    // T-53: ちょうど 1 つ → そのコード（部門以外のグループは数えない）
    [Theory]
    [InlineData("/department/engineering", "engineering")]
    [InlineData("/clearance/restricted,/department/sales", "sales")]
    [InlineData("/department/hr,/clearance/internal,/teams/hr", "hr")]
    public void ExactlyOneDepartmentGroup_YieldsItsCode(string pathList, string expected)
    {
        RegistrantDepartment.FromGroupPaths(Split(pathList)).Should().Be(expected);
    }

    // T-54: 部門グループに属さない → 導かない（null ＝ 呼び出し側で予約値 unassigned へ倒れる）
    [Theory]
    [InlineData("")]
    [InlineData("/clearance/restricted")]
    // 🔴 名前だけ一致しても別の木（/teams/sales）は部門ではない —— 名前で突き合わせると誤った部門を作る。
    [InlineData("/teams/sales")]
    // 親グループ（/department そのもの）への所属は部門ではない。
    [InlineData("/department")]
    // `/department/` 直下が空の不正なパスも数えない。
    [InlineData("/department/")]
    // 大小文字は畳まない（Keycloak のグループ名は大小文字を区別する）。
    [InlineData("/Department/sales")]
    // realm に group-paths マッパーが無い（クレームが無い）場合は名前だけの groups を渡されても導かない。
    [InlineData("sales")]
    public void NoDepartmentGroup_YieldsNull(string pathList)
    {
        RegistrantDepartment.FromGroupPaths(Split(pathList)).Should().BeNull();
    }

    // T-55: 2 つ以上 → 導かない。
    // 🔴 変異: 「複数でも先頭を採る」へ変えるとここが赤になる（2 部門の人の登録を片方へ寄せるのは推測である）。
    [Theory]
    [InlineData("/department/engineering,/department/sales")]
    [InlineData("/department/sales,/department/engineering")]
    [InlineData("/department/hr,/clearance/internal,/department/sales,/department/engineering")]
    public void TwoOrMoreDepartmentGroups_YieldsNull(string pathList)
    {
        RegistrantDepartment.FromGroupPaths(Split(pathList)).Should().BeNull();
    }

    // T-58: 入れ子は上位の部門コードに畳む。同じ部門の入れ子は 1 つ、異なる部門にまたがれば 2 つ。
    [Theory]
    [InlineData("/department/engineering/backend", "engineering")]
    [InlineData("/department/engineering,/department/engineering/backend", "engineering")]
    [InlineData("/department/engineering/backend,/department/sales", null)]
    public void NestedGroups_FoldToTopLevelDepartment(string pathList, string? expected)
    {
        RegistrantDepartment.FromGroupPaths(Split(pathList)).Should().Be(expected);
    }

    // T-59: Keycloak の group-membership マッパーは `group_paths` を **JSON 配列**で発行する。
    // 本物のトークン処理器（JwtBearer が使う JsonWebTokenHandler・既定の受信クレーム写像あり）を通すと
    // **1 値 1 クレーム**になり、登録端点の読み口がそれを全部拾うことを固定する
    // （`FindFirst` で読むと先頭 1 つに畳まれ、2 部門の人が「1 つ」に見えて誤った部門を作る）。
    [Theory]
    [InlineData("/clearance/restricted,/department/engineering", "engineering")]
    [InlineData("/department/engineering,/department/sales", null)]
    public async Task KeycloakShapedJwt_ArrayClaim_IsReadAsMultipleClaims(string pathList, string? expected)
    {
        var paths = Split(pathList);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("datasource-registrant-dept-test-key-0123456789abcdef"));
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://test-issuer/realms/platform",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["preferred_username"] = "alice",
                ["group_paths"] = paths,
            },
        });

        var result = await new JsonWebTokenHandler { MapInboundClaims = true }.ValidateTokenAsync(token,
            new TokenValidationParameters
            {
                ValidIssuer = "https://test-issuer/realms/platform",
                ValidateAudience = false,
                IssuerSigningKey = key,
            });

        result.IsValid.Should().BeTrue();
        var user = new ClaimsPrincipal(result.ClaimsIdentity);
        user.FindAll(CreateDataSourceEndpoint.GroupPathsClaim).Should().HaveCount(paths.Length,
            "配列クレームは同じ型の複数クレームになる（受信クレーム写像で名前が変わらない）");
        CreateDataSourceEndpoint.RegistrantDepartmentOf(user).Should().Be(expected);
    }

    // #754 監査（生き残った変異 M2b / M9 / M11 を殺す）
    //
    // M2b: 根の照合から末尾の `/` を落とす（`/department` の前方一致）と、`/departmentX/sales` や
    //      `/department-archive/x` が部門木に見えてしまう。**別の木であり、部門ではない。**
    [Theory]
    [InlineData("/departmentX/sales")]
    [InlineData("/department-archive/x")]
    [InlineData("/departments/sales")]
    public void SiblingTreesSharingThePrefix_AreNotDepartments(string pathList)
    {
        RegistrantDepartment.FromGroupPaths(Split(pathList)).Should().BeNull();
    }

    // M9: 部門コードを大小文字無視で束ねると `Sales` と `sales` が 1 つに見える。Keycloak のグループ名は
    //     大小文字を区別するので**別の 2 部門**であり、2 つに属する登録者からは導かない。
    [Fact]
    public void CodesDifferingOnlyInCase_AreTwoDistinctDepartments()
    {
        RegistrantDepartment.FromGroupPaths(["/department/Sales", "/department/sales"]).Should().BeNull();
    }

    // M11: 入れ子の畳み方を「最後の段」や「直下以降すべて」へ変えると、深い入れ子で部門コードがずれる。
    //      何段深くても**部門木の直下の 1 段**が部門コードである。
    [Theory]
    [InlineData("/department/engineering/backend/api", "engineering")]
    [InlineData("/department/engineering/backend/api,/department/engineering", "engineering")]
    public void DeeplyNestedGroups_FoldToTheFirstSegmentUnderTheRoot(string pathList, string expected)
    {
        RegistrantDepartment.FromGroupPaths(Split(pathList)).Should().Be(expected);
    }

    // 属性引数に単独の配列を渡せない（CS0182）ため、カンマ区切りで受けて分ける。空文字は 0 個。
    private static string[] Split(string pathList) =>
        pathList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
