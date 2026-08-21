using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Knowledge.IntegrationTests.Deployment;

// IADR-0017（Superseded by IADR-0026） / #62 / #100, FR-05, NFR(機密性), ADR-0005:
// ネットワーク分離はもはや「第一防御」ではない（第一防御は Istio STRICT mTLS。MeshMtlsTests 参照）。
// IADR-0026 により、docker-compose（ローカル開発ランタイム）の host 非公開は「多層防御
// （defense-in-depth）」として維持する。アプリのエッジ入口は BFF（＋フロントエンド SPA。IADR-0033）。
// dev 便宜として Wiki.js(3001) の host 公開を IADR-0032 が IADR-0026 §2 を dev 範囲で改定して許容するが、
// **本番系（Helm）では Wiki.js を Ingress 公開しない**ことを本テストが回帰ガードする。
// 本テストは compose の内部サービス再公開と、本番系での Wiki.js 迂回公開の回帰を多層防御として防ぐ。
[Trait("Category", "Deployment")]
public sealed class NetworkIsolationTests
{
    // IADR-0017: host 公開を許すのはエッジ(BFF)と、開発利便のためのインフラ系のみ。
    // 下記のアプリ内部サービスは host `ports:` を公開してはならない（expose のみ）。
    private static readonly string[] InternalAppServices =
    [
        "document-service",
        "datasource-service",
        "retrieval-service",   // /search の ABAC は #55 で別管理だが host 公開停止は一律適用
        "aianalysis-service",
        "authorization-service",
        "wiki-service",
        "llm-gateway",
        "feedback-service",
        "dashboard-service",
        "configuration-service", // Issue #283, IADR-0070: AST 設定画面の後段。内部 API（expose のみ）を回帰ガード。
        "risk-management-service", // Issue #287, IADR-0071: AST リスク設定/統制状態の後段。内部 API（expose のみ）を回帰ガード。
        "market-monitor-service", // Issue #288, IADR-0072: AST 監視銘柄（watchlist）の後段。内部 API（expose のみ）を回帰ガード。
        // FR-12, SC-07, Issue #501, IADR-0128 決定3: 変換ジョブ API（/jobs・retry 含む）の後段。
        // ConversionService は **アプリ層の認可を課さない**（IADR-0042 決定3 / IADR-0029 の最小 HTTP サーフェス）。
        // その代償統制がネットワーク分離であるにもかかわらず本列挙から漏れており、host 公開の回帰を
        // 誰も止められなかった。BFF の retry を管理者限定へ絞っても、後段へ直接到達できれば同じ穴が残る。
        "conversion-service",
        // FR-12, SC-07, Issue #501: **`ingestion-service` は意図的に「未対応」であって「公開してよい」ではない**。
        // 同じく compose では expose のみだが本列挙に無く、host 公開の回帰を止められない（同型の穴）。
        // 今回入れなかったのは、HTTP サーフェスが MapPlatformIntrospection() 1 件のみで副作用のある操作を
        // 持たず、FR-12 / SC-07 の射程（retry の権限是正）外だからである（IADR-0128 フォローアップ 2）。
        // 追加するときはここへ 1 行足す。
    ];

    [Fact]
    public void InternalServices_MustNotPublishHostPorts()
    {
        var compose = ReadComposeFile();
        var blocks = SplitServiceBlocks(compose);

        foreach (var svc in InternalAppServices)
        {
            blocks.Should().ContainKey(svc,
                $"'{svc}' が docker-compose.yml に存在すること");

            // IADR-0017: 内部サービスは host 公開（ports:）せず expose のみ。
            blocks[svc].Should().NotMatchRegex(@"(?m)^\s*ports:\s*$",
                $"IADR-0017: 内部サービス '{svc}' は host ポートを公開してはならない（expose を用いる）");
        }
    }

    [Fact]
    public void Bff_RemainsTheOnlyPublishedAppEdge()
    {
        var compose = ReadComposeFile();
        var blocks = SplitServiceBlocks(compose);

        // エッジ(BFF)は外部からの唯一の入口として host 公開を維持する。
        blocks.Should().ContainKey("bff");
        blocks["bff"].Should().MatchRegex(@"(?m)^\s*ports:\s*$",
            "BFF は外部からの入口として host 公開を維持する");
        blocks["bff"].Should().Contain("5000:8080");
    }

    // FR-12, SC-07, Issue #501, IADR-0128 決定3: compose の host 非公開は代償統制の 1 本にすぎない。
    // 「外部から後段へ到達できない」の論拠は 本番系（Helm）の Service が ClusterIP であること・
    // NetworkPolicy が既定 deny であること・Istio VirtualService に経路が無いこと にも支えられている。
    // このうち **最も起こりやすい公開経路である Service の type** を機械検査に載せる（残り 2 本は
    // IADR-0128 フォローアップ 4）。service.yaml は .Values.services 全件を 1 枚の range で描画するため、
    // ここに type: が現れないこと＝全内部サービスが既定の ClusterIP であることを意味する。
    [Fact]
    public void InternalServices_HelmServicesMustStayClusterIp()
    {
        var template = ReadHelmServiceTemplate();

        // type: を書かない（＝ ClusterIP）。値で差し替えられる形（type: {{ ... }}）も禁止する。
        template.Should().NotMatchRegex(@"(?m)^\s*type:",
            "IADR-0128 決定3: 内部サービスの Service は既定 ClusterIP のままとする"
            + "（NodePort / LoadBalancer 化は BFF 以外の公開エッジを作る）。"
            + "将来 type を values で持たせる場合は、本検査を values 側の検査へ置き換えること");
        template.Should().NotMatchRegex(@"(?m)^\s*nodePort:",
            "IADR-0128 決定3: 内部サービスに nodePort を割り当ててはならない");
    }

    // IADR-0032 (#124): dev（compose）は Wiki.js 管理 UI への直接アクセス便宜のため 3001 を公開する
    // （dev 公開は残す）。この dev 便宜の公開は wiki-js に限定され、他の内部アプリサービスへは波及しない
    // ことを InternalServices_MustNotPublishHostPorts が引き続き保証する。
    [Fact]
    public void WikiJs_DevExposureIsRetainedOnComposeOnly()
    {
        var compose = ReadComposeFile();
        var blocks = SplitServiceBlocks(compose);

        blocks.Should().ContainKey("wiki-js", "'wiki-js' が docker-compose.yml に存在すること");
        blocks["wiki-js"].Should().Contain("3001:3000",
            "dev 便宜のため wiki-js は 3001 を公開する（dev 公開は残す。本番系は Helm で非公開）");
    }

    // IADR-0020 / IADR-0032 (#124): **本番系（Helm）では** Wiki.js を Ingress で公開しない
    // （既定 wikijs.ingress.enabled: false）。ゲートウェイ迂回の外部到達を本番系で塞ぐ回帰ガード。
    [Fact]
    public void WikiJs_HelmIngressDisabledByDefault()
    {
        var wikijs = ReadHelmWikijsValues();

        wikijs.Should().MatchRegex(@"(?m)^\s*ingress:\s*$",
            "wikijs に ingress ブロックが存在すること");
        // ingress: ブロック直下の enabled: が false であること（既定で Ingress を生やさない）。
        wikijs.Should().MatchRegex(@"ingress:\s*\n\s*enabled:\s*false",
            "IADR-0032: stg/prod で Wiki.js を Ingress 公開してはならない（既定 enabled: false）");
    }

    // --- helpers ---------------------------------------------------------

    private static string ReadComposeFile() =>
        File.ReadAllText(ResolveRepoFile(Path.Combine("deploy", "docker-compose.yml")));

    // Helm の Service テンプレート（.Values.services 全件を range で描画する 1 枚）。
    private static string ReadHelmServiceTemplate() =>
        File.ReadAllText(ResolveRepoFile(Path.Combine(
            "deploy", "helm", "microservices-platform", "templates", "service.yaml")));

    // Helm values.yaml から wikijs トップレベルブロック（次のトップレベルキーまで）を抽出する。
    private static string ReadHelmWikijsValues()
    {
        var values = File.ReadAllText(ResolveRepoFile(
            Path.Combine("deploy", "helm", "microservices-platform", "values.yaml")));
        var lines = values.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^wikijs:\s*$"));
        if (start < 0)
            throw new InvalidOperationException("values.yaml に wikijs: ブロックが見つかりません。");

        var buf = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            // インデント無しの非空行（次のトップレベルキー）で終端。
            if (Regex.IsMatch(lines[i], @"^[a-zA-Z]"))
                break;
            buf.Add(lines[i]);
        }
        return string.Join("\n", buf);
    }

    private static string ResolveRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"{relative} をリポジトリルートから解決できませんでした。");
    }

    // `services:` 配下の各サービス（2 スペースインデントの `name:`）を、次のサービスまでの本文へ分割する。
    private static Dictionary<string, string> SplitServiceBlocks(string compose)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = compose.Replace("\r\n", "\n").Split('\n');

        // トップレベル `services:` 以降を対象にする。
        var start = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^services:\s*$"));
        if (start < 0) return result;

        // サービス名は "  name:"（2 スペース）で始まる。volumes: 等のトップレベルキーで終端する。
        var header = new Regex(@"^  (?<name>[a-z0-9-]+):\s*$");
        string? current = null;
        var buf = new List<string>();

        void Flush()
        {
            if (current is not null)
                result[current] = string.Join("\n", buf);
        }

        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            // トップレベルキー（インデント無し・非空）に到達したら services セクション終了。
            if (Regex.IsMatch(line, @"^[a-z]"))
            {
                Flush();
                current = null;
                break;
            }
            var m = header.Match(line);
            if (m.Success)
            {
                Flush();
                current = m.Groups["name"].Value;
                buf = [];
                continue;
            }
            if (current is not null)
                buf.Add(line);
        }
        Flush();
        return result;
    }
}
