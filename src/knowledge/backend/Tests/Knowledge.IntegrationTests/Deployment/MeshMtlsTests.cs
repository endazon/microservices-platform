using System.Text.RegularExpressions;
using FluentAssertions;

namespace Knowledge.IntegrationTests.Deployment;

// IADR-0026 / #100, NFR(機密性), ADR-0005:
// サービス間認証の第一防御は Istio STRICT mTLS。Helm の Istio マニフェスト
// (deploy/helm/knowledge-platform/templates/istio-mtls.yaml) が STRICT mTLS を宣言し、
// 平文許容(PERMISSIVE/DISABLE)へ後退していないことを回帰として固定する
// （IADR-0017 のネットワーク分離を第一防御とする暫定運用の解消を担保）。
[Trait("Category", "Deployment")]
public sealed class MeshMtlsTests
{
    [Fact]
    public void PeerAuthentication_EnforcesStrictMtls()
    {
        var manifest = ReadHelmTemplate("istio-mtls.yaml");

        // PeerAuthentication が宣言されていること。
        manifest.Should().MatchRegex(@"kind:\s*PeerAuthentication",
            "ADR-0005: サービス間 mTLS を強制する PeerAuthentication を宣言すること");

        // mTLS モードは values 既定 STRICT（平文フォールバック無し）。
        // テンプレートは {{ .Values.mesh.mtlsMode }} を参照し、既定値は values.yaml で STRICT。
        manifest.Should().MatchRegex(@"mode:\s*\{\{\s*\.Values\.mesh\.mtlsMode\s*\}\}",
            "PeerAuthentication の mtls.mode は values.mesh.mtlsMode を参照すること");
    }

    [Fact]
    public void DestinationRule_UsesIstioMutualTls()
    {
        var manifest = ReadHelmTemplate("istio-mtls.yaml");

        manifest.Should().MatchRegex(@"kind:\s*DestinationRule",
            "ADR-0005: 送信側 TLS を固定する DestinationRule を宣言すること");
        manifest.Should().MatchRegex(@"mode:\s*ISTIO_MUTUAL",
            "DestinationRule の trafficPolicy.tls.mode は ISTIO_MUTUAL であること");
    }

    [Fact]
    public void MtlsMode_DefaultsToStrict_NoPlaintextFallback()
    {
        var values = ReadHelmFile("values.yaml");

        // 恒久像（IADR-0026）: 既定の mTLS モードは STRICT。
        values.Should().MatchRegex(@"(?m)^\s*mtlsMode:\s*STRICT\b",
            "IADR-0026: mesh.mtlsMode の既定は STRICT（平文フォールバック無し）であること");

        // 平文許容モードを既定にしていないこと（移行期の一時措置のみ許容）。
        Regex.IsMatch(values, @"(?m)^\s*mtlsMode:\s*(PERMISSIVE|DISABLE)\b")
            .Should().BeFalse("既定の mesh.mtlsMode を PERMISSIVE/DISABLE にしてはならない");
    }

    [Fact]
    public void Namespace_EnablesIstioSidecarInjection()
    {
        var manifest = ReadHelmTemplate("namespace.yaml");

        manifest.Should().Contain("istio-injection: enabled",
            "ADR-0005: STRICT mTLS の前提として Namespace にサイドカー自動注入ラベルを付与すること");
    }

    // --- helpers ---------------------------------------------------------

    private static string ReadHelmTemplate(string fileName) =>
        ReadHelmFile(Path.Combine("templates", fileName));

    private static string ReadHelmFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "deploy", "helm", "knowledge-platform", relative);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"deploy/helm/knowledge-platform/{relative} をリポジトリルートから解決できませんでした。");
    }
}
