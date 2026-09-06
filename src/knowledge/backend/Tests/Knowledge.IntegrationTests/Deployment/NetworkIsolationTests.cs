using System.Text.RegularExpressions;
using AwesomeAssertions;
using Knowledge.IntegrationTests.Fixtures;

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
        // FR-22, ADR-0045, Issue #1025: 個人資料の通知の受け口（POST /internal/notifications）の後段。
        // 配備と同時にここへ足す —— 受け口は認証済み内部呼び出しだけを想定しており、host 公開されると
        // 誰でも利用者宛の通知を投函できる。送出側は fail-open なので、穴が開いても不達としては現れない。
        "notification-service",
        // NFR-09, Issue #458, IADR-0403 決定 6: 以下 3 本は **compose の側から母集合を引き直して**見つけた
        // 列挙漏れである（記憶で挙げず `build:` を持つサービスを機械的に数えた。traceability.repo.md 規則 9）。
        // いずれも**実態としては正しく expose のみ**で、Helm でも ClusterIP（ingress ブロックを持つのは
        // wikijs だけ・既定 enabled: false）である。**つまり穴は開いていない。開いても止められなかった**
        // ——`conversion-service` のとき（上記 :33-37）と同じ形の欠陥であり、その 2 回目・3 回目にあたる。
        // 再発は EveryComposeAppService_MustBeClassified が構造で止める（列挙漏れが fail-closed になる）。
        "graph-service",   // FR-17, UC-10, ADR-0033/ADR-0034: 知識グラフ。
        "mcp-service",     // SC-12: MCP のエッジ集約（/mcp）と管理 REST。chart のキーは `mcp`。
        // **`ingestion-service` は意図的に「未対応」であって「公開してよい」ではない**と旧コメントは
        // 述べていた（IADR-0128 フォローアップ 2）。除外の理由（HTTP サーフェスが
        // MapPlatformIntrospection() 1 件のみで副作用のある操作を持たない）は **2026-09-06 の実測でも
        // 成り立つ**が、🔴 **本列挙の目的は「危ない口を持つか」ではなく「host 公開してよいか」である。**
        // 公開してよくない以上、ここに居るのが正しい（IADR-0403 決定 6）。
        "ingestion-service",
    ];

    // NFR-09, Issue #458, IADR-0403 決定 6: **host 公開してよい縁**。ここに書くことが公開の唯一の根拠になる。
    // 🔴 wiki-js は第三者イメージ（image:）なので下の分類対象に入らない —— dev 便宜の 3001 公開は
    // IADR-0032 が決め、WikiJs_DevExposureIsRetainedOnComposeOnly が別途固定している。
    private static readonly string[] HostPublishedEdges =
    [
        "bff",       // アプリの唯一のエッジ入口（5000:8080）。
        "frontend",  // SPA エッジ（IADR-0033）。
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

    // NFR-09, Issue #458, IADR-0403 決定 6: 🔴 **列挙の既定を fail-open から fail-closed へ裏返す。**
    //
    // これまで InternalAppServices は手で維持する列挙であり、**新しい内部サービスを compose へ足して
    // ここへ足し忘れると、CI は黙って通った**。実際にそれが 3 回起きている（conversion-service は
    // #501 で埋めた 1 回目、graph-service / mcp-service が #458 で見つけた 2・3 回目）。
    //
    // 🔴 **これは検査器の新設ではない**（CLAUDE.md「同型の事故が 2 回起きたら」の対象外）——
    // 既に在る NetworkIsolationTests の**母集合の取り方を、記憶から compose の実体へ移す**ものである。
    //
    // 第一者のアプリサービスは **`build:` を持つこと**で第三者インフラ（`image:`）と機械的に区別できる。
    // その全件が「内部（InternalAppServices）」か「公開してよい縁（HostPublishedEdges）」の
    // **どちらかに必ず属する**ことを課す。どちらでもないサービスが現れたら落ちる。
    [Fact]
    public void EveryComposeAppService_MustBeClassified()
    {
        var compose = ReadComposeFile();
        var blocks = SplitServiceBlocks(compose);

        // `build:` を持つ＝このリポジトリがビルドする第一者サービス。
        var appServices = blocks
            .Where(kv => Regex.IsMatch(kv.Value, @"(?m)^\s{4}build:\s*$"))
            .Select(kv => kv.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // 🔴 母集合が壊れた（パーサが何も拾わない）ときに緑で通さない。
        appServices.Should().HaveCountGreaterThan(10,
            "compose の第一者アプリサービスを拾えていること（パーサが壊れたら 0 件で緑になってしまう）");

        var classified = InternalAppServices.Concat(HostPublishedEdges).ToHashSet(StringComparer.Ordinal);
        var unclassified = appServices.Where(s => !classified.Contains(s)).ToList();

        unclassified.Should().BeEmpty(
            "IADR-0403 決定 6: compose の第一者アプリサービスは、内部（InternalAppServices）か "
            + "host 公開の縁（HostPublishedEdges）のどちらかへ必ず分類すること。"
            + "未分類のまま増えると host 公開の回帰を誰も止められない（#458 で 3 件見つかった穴と同型）");

        // 逆向き: 列挙に書いたのに compose に居ないサービス（改名・削除の取り残し）も落とす。
        var composeNames = blocks.Keys.ToHashSet(StringComparer.Ordinal);
        var stale = InternalAppServices.Concat(HostPublishedEdges)
            .Where(s => !composeNames.Contains(s)).ToList();
        stale.Should().BeEmpty("列挙にあるが compose に無いサービス（改名・削除の取り残し）を残さないこと");
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

    private static string ResolveRepoFile(string relative) => RepoFile.Find(relative);

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
