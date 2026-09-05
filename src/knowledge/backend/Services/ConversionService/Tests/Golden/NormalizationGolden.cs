using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConversionService.Domain.Ports;
using ConversionService.Features.ConversionJobs.Normalize;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConversionService.Tests;

// FR-12, UC-06, ADR-0012/0014, IADR-0298: 正規化変換のゴールデンファイル器。
//
// **何を固定し、何を固定していないか**（IADR-0298 決定 2。テスト仕様書 T-14〜T-18 と同じ表）:
//
//   固定する … 正規化 Markdown の全文 / 決定的 DocumentId の実値 / 資産キーの全文 /
//              資産のバイト長と SHA-256 / コード化・保持の件数 / 図 1 つ 1 つの記録 /
//              **IDiagramCoder へ渡した機密区分**（出力に現れないため他のどのテストでも見えない）。
//
//   固定しない … 🔴 **pandoc の変換そのもの**。入力（`Cases/<name>.body.md`）は
//                「変換器がこう出すであろう Markdown」を人が書いたものであり、**pandoc の実際の
//                出力ではない**。原本（docx / PDF / HTML のバイナリ）は 1 バイトも読んでいない。
//                したがって「PDF のゴールデンテストがある」とは言えない —— あるのは
//                **「PDF 由来と宣言された変換器出力を正規化した結果」のゴールデン**である。
//
// 差し替えは `IADR-0008` が置いた 3 ポート（IBodyConverter / IDiagramCoder / IObjectStore）の
// 境界で行う。本器が新しい接ぎ目を作っているわけではない。
//
// 🔴 IADR-0351 (#1120): 変換器は `--extract-media` 由来の参照を **`![fig-N](figure:fig-N)` の目印**へ
// 書き換えて返すようになった。`Cases/<name>.body.md` は「変換器がこう出すであろう Markdown」なので、
// **目印を含む case（`html-article` / `office-docx-report`）と含まない case（`pdf-report`）の両方**を
// 置いてある —— 前者は図が**本文中の元の位置**へ入ること、後者は目印が無いときに**末尾へ append**
// する経路（IADR-0351 決定 6）を固定する。決定 2「pandoc は実走させない」は変えていない。
//
// **golden の更新は手で書き換えない**（IADR-0298 決定 4）:
//   UPDATE_GOLDEN=1 dotnet test src/knowledge/backend/backend.slnx \
//     --filter "FullyQualifiedName~NormalizationServiceTests"
// 更新モードは**書き込んだうえでテストを失敗させる**。「書いて緑」にすると、変数が CI の環境へ
// 紛れ込んだときに差分を無条件で飲み込んで緑になる（`PandocConversionServiceTests` が名指しした
// 「走らなかったケースを Passed として報告する」と同じ型の事故である）。
internal static class NormalizationGolden
{
    /// <summary>golden を書き戻すモードに入る環境変数。値が空でなければ更新モードである。</summary>
    internal const string UpdateEnvironmentVariable = "UPDATE_GOLDEN";

    private const string BeginMarker = "--8<-- markdown begin";
    private const string EndMarker = "--8<-- markdown end";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>本ファイルの所在からゴールデン資材のディレクトリを解く（ビルド出力へコピーしない）。</summary>
    private static string GoldenDir([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(thisFile)!;

    private static string CasesDir => Path.Combine(GoldenDir(), "Cases");

    private static string ExpectedDir => Path.Combine(GoldenDir(), "Expected");

    /// <summary>`Cases/<name>.json` のベース名を順序固定で返す。</summary>
    internal static IReadOnlyList<string> CaseNames() =>
        Directory.Exists(CasesDir)
            ? [.. Directory.EnumerateFiles(CasesDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal)]
            : [];

    /// <summary>`Expected/<name>.golden.md` のベース名を順序固定で返す（孤児検出に使う）。</summary>
    internal static IReadOnlyList<string> GoldenNames() =>
        Directory.Exists(ExpectedDir)
            ? [.. Directory.EnumerateFiles(ExpectedDir, "*.golden.md")
                .Select(p => Path.GetFileName(p)![..^".golden.md".Length])
                .OrderBy(n => n, StringComparer.Ordinal)]
            : [];

    /// <summary>
    /// case を読み、正規化を実行し、ゴールデン表現へ描画する。
    /// 外部プロセス・ネットワーク・実ストレージには一切触れない。
    /// </summary>
    internal static async Task<string> RenderAsync(string caseName)
    {
        var spec = ReadCase(caseName);
        var body = ReadBody(caseName);

        var figures = spec.Figures
            .Select(f => new ExtractedFigure(f.FigureId, f.ImageContentType, DecodeBytes(f))
            {
                Caption = f.Caption
            })
            .ToList();

        var coder = new ScriptedDiagramCoder(spec.Figures);
        var store = new GoldenObjectStore();
        // ADR-0070 決定 3 / IADR-0356 (#1192) / [[IADR-0381]]: case が `hasBody: false` を宣言していれば、変換器が
        // 「テキスト層なし」を返したとみなす（`pdf-no-text-layer`）。抽出器そのものは実走させない
        // （決定 2 と同じ理由。空判定は `PdfTextLayerConverterTests` が持つ）。
        var service = new NormalizationService(
            new ScriptedBodyConverter(new BodyConversionResult(body, figures) { HasBody = spec.HasBody }),
            coder,
            store,
            NullLogger<NormalizationService>.Instance);

        var raw = new RawDocumentFetched(
            FetchId: spec.FetchId,
            SourceId: spec.SourceId,
            SourceType: spec.SourceType,
            OriginalPath: spec.OriginalPath,
            StorageUri: spec.StorageUri,
            ContentType: spec.ContentType,
            Attributes: new Dictionary<string, string>(spec.Attributes, StringComparer.Ordinal),
            Tags: [.. spec.Tags],
            // 変換時刻は正規化結果に現れない（`DocumentNormalized` の発行は購読側の責務）。
            // 決定性のため固定値を置く。
            FetchedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var result = await service.NormalizeAsync(raw, TestContext.Current.CancellationToken);

        return Render(caseName, spec, result, store, coder);
    }

    /// <summary>golden と突き合わせる。更新モードでは書き戻したうえで失敗させる。</summary>
    internal static void Verify(string caseName, string actual)
    {
        var path = Path.Combine(ExpectedDir, $"{caseName}.golden.md");
        var updating = !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable(UpdateEnvironmentVariable));

        if (!File.Exists(path))
        {
            if (!updating)
            {
                // fail-closed: 黙って作らない。無い golden を作って緑にすると、初回だけ
                // 「何であれ現在の出力」が正になる。
                Assert.Fail($"golden が無い: {path}\n" +
                    $"{UpdateEnvironmentVariable}=1 で生成し、内容をレビューしてからコミットすること。");
            }

            Directory.CreateDirectory(ExpectedDir);
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            Assert.Fail($"golden を生成した: {path}\n" +
                $"差分をレビューし、{UpdateEnvironmentVariable} を外して再実行すること。");
        }

        var expected = Normalize(File.ReadAllText(path));
        if (expected == actual) return;

        if (updating)
        {
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            Assert.Fail($"golden を更新した: {path}\n" +
                $"差分をレビューし、{UpdateEnvironmentVariable} を外して再実行すること。");
        }

        Assert.Fail($"正規化結果が golden と一致しない: {path}\n" +
            $"意図した変更なら {UpdateEnvironmentVariable}=1 で書き戻し、差分を PR に載せること。\n" +
            $"--- expected ---\n{expected}\n--- actual ---\n{actual}");
    }

    // --- 描画 ---------------------------------------------------------------------------

    private static string Render(string caseName, GoldenCaseSpec spec, NormalizationResult result,
        GoldenObjectStore store, ScriptedDiagramCoder coder)
    {
        var markdown = store.LastMarkdown;
        var sb = new StringBuilder();

        sb.Append("# golden: ").Append(caseName).Append('\n');
        sb.Append("# ").Append(spec.Description).Append('\n');
        sb.Append("# 原本は読んでいない。入力は変換器出力を模した Markdown である（IADR-0298 決定 2）。\n");
        sb.Append('\n');

        sb.Append("## input\n");
        sb.Append("contentType     : ").Append(spec.ContentType).Append('\n');
        sb.Append("originalPath    : ").Append(spec.OriginalPath).Append('\n');
        sb.Append("storageUri      : ").Append(spec.StorageUri).Append('\n');
        sb.Append("confidentiality : ").Append(
            spec.Attributes.TryGetValue("confidentiality", out var c) ? c : "(unset)").Append('\n');
        sb.Append('\n');

        sb.Append("## result\n");
        sb.Append("documentId      : ").Append(result.DocumentId.ToString("D")).Append('\n');
        sb.Append("markdownKey     : ").Append(store.LastMarkdownKey).Append('\n');
        sb.Append("markdownUri     : ").Append(result.MarkdownUri).Append('\n');
        sb.Append("diagramsCoded   : ").Append(result.DiagramsCoded).Append('\n');
        sb.Append("diagramsRetained: ").Append(result.DiagramsRetained).Append('\n');
        // ADR-0070 決定 3: 本文の有無は succeeded の内訳。正規化結果が運ぶ値なので golden に載せる。
        sb.Append("hasBody         : ").Append(result.HasBody ? "true" : "false").Append('\n');
        sb.Append("markdownLength  : ").Append(markdown.Length).Append('\n');
        sb.Append("markdownSha256  : ").Append(Sha256(Encoding.UTF8.GetBytes(markdown))).Append('\n');
        sb.Append('\n');

        sb.Append("## assets\n");
        if (store.SavedAssets.Count == 0)
        {
            sb.Append("(none)\n");
        }
        else
        {
            for (var i = 0; i < store.SavedAssets.Count; i++)
            {
                var a = store.SavedAssets[i];
                sb.Append(i + 1).Append(") key=").Append(a.Key)
                    .Append(" uri=").Append(a.Uri)
                    .Append(" contentType=").Append(a.ContentType)
                    .Append(" bytes=").Append(a.Bytes.Length)
                    .Append(" sha256=").Append(Sha256(a.Bytes)).Append('\n');
            }
        }
        sb.Append('\n');

        // G-7: 機密区分の受け渡し。**正規化結果には現れない**ため、ここで記録しないと
        // 「送信制御を黙って外した」変更が golden にも他のテストにも映らない。
        sb.Append("## diagramCoderCalls\n");
        if (coder.Calls.Count == 0)
        {
            sb.Append("(none)\n");
        }
        else
        {
            for (var i = 0; i < coder.Calls.Count; i++)
            {
                var call = coder.Calls[i];
                sb.Append(i + 1).Append(") figureId=").Append(call.FigureId)
                    .Append(" confidentiality=").Append(call.Confidentiality ?? "(null)").Append('\n');
            }
        }
        sb.Append('\n');

        sb.Append("## figures\n");
        if (result.Figures.Count == 0)
        {
            sb.Append("(none)\n");
        }
        else
        {
            for (var i = 0; i < result.Figures.Count; i++)
            {
                var f = result.Figures[i];
                sb.Append(i + 1).Append(") id=").Append(f.FigureId)
                    .Append(" coded=").Append(f.Coded ? "true" : "false")
                    .Append(" language=").Append(Escape(f.Language))
                    .Append(" code=").Append(Escape(f.Code))
                    .Append(" imageUri=").Append(Escape(f.ImageUri))
                    .Append(" imageContentType=").Append(Escape(f.ImageContentType))
                    .Append(" caption=").Append(Escape(f.Caption)).Append('\n');
            }
        }
        sb.Append('\n');

        sb.Append("## markdown\n");
        sb.Append(BeginMarker).Append('\n');
        sb.Append(markdown);
        if (!markdown.EndsWith('\n')) sb.Append('\n');
        sb.Append(EndMarker).Append('\n');

        return Normalize(sb.ToString());
    }

    /// <summary>改行を LF へ寄せる。末尾の改行は 1 つに固定する（`insert_final_newline` と整合）。</summary>
    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n') + "\n";

    /// <summary>null と改行を可視化する（1 行 1 図の形を崩さないため）。</summary>
    private static string Escape(string? value) => value is null
        ? "(null)"
        : "|" + value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r") + "|";

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // --- case の読み込み ------------------------------------------------------------------

    private static GoldenCaseSpec ReadCase(string caseName)
    {
        var path = Path.Combine(CasesDir, $"{caseName}.json");
        var spec = JsonSerializer.Deserialize<GoldenCaseSpec>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"case を読めない: {path}");
        return spec;
    }

    private static string ReadBody(string caseName)
    {
        var path = Path.Combine(CasesDir, $"{caseName}.body.md");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    /// <summary>図の画像バイト列。宣言が無ければ figureId から決定的に作る（値そのものに意味は無い）。</summary>
    private static byte[] DecodeBytes(GoldenFigureSpec figure) =>
        figure.ImageBase64 is { Length: > 0 } b64
            ? Convert.FromBase64String(b64)
            : Encoding.UTF8.GetBytes(figure.FigureId);

    // --- 差し替え（3 ポートすべて） ---------------------------------------------------------

    private sealed class ScriptedBodyConverter(BodyConversionResult result) : IBodyConverter
    {
        public Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
            CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class ScriptedDiagramCoder(IReadOnlyList<GoldenFigureSpec> figures) : IDiagramCoder
    {
        internal List<(string FigureId, string? Confidentiality)> Calls { get; } = [];

        public Task<DiagramCodingResult> CodeAsync(ExtractedFigure figure, string? confidentiality,
            CancellationToken ct = default)
        {
            Calls.Add((figure.FigureId, confidentiality));
            var spec = figures.FirstOrDefault(f => f.FigureId == figure.FigureId)
                ?? throw new InvalidOperationException($"case に図 {figure.FigureId} の宣言が無い");
            return Task.FromResult(spec.Coded
                ? DiagramCodingResult.Success(spec.Language!, spec.Code!)
                : DiagramCodingResult.Retain(spec.Reason ?? "not-codeable"));
        }
    }

    private sealed class GoldenObjectStore : IObjectStore
    {
        internal string LastMarkdown { get; private set; } = string.Empty;

        internal string LastMarkdownKey { get; private set; } = string.Empty;

        internal List<SavedAsset> SavedAssets { get; } = [];

        public Task<string> SaveMarkdownAsync(string key, string markdown, CancellationToken ct = default)
        {
            LastMarkdown = markdown;
            LastMarkdownKey = key;
            return Task.FromResult($"storage://normalized/{key}");
        }

        public Task<string> SaveAssetAsync(string key, byte[] bytes, string contentType,
            CancellationToken ct = default)
        {
            var uri = $"storage://normalized/{key}";
            SavedAssets.Add(new SavedAsset(key, uri, contentType, bytes));
            return Task.FromResult(uri);
        }

        public Task<string?> TryGetMarkdownAsync(string uri, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }

    internal sealed record SavedAsset(string Key, string Uri, string ContentType, byte[] Bytes);
}

/// <summary>`Cases/&lt;name&gt;.json` の宣言。本文は `Cases/&lt;name&gt;.body.md` に分けて置く。</summary>
internal sealed class GoldenCaseSpec
{
    public string Description { get; set; } = string.Empty;

    public Guid FetchId { get; set; } = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

    public Guid SourceId { get; set; }

    public string SourceType { get; set; } = "filesystem";

    public string OriginalPath { get; set; } = string.Empty;

    public string StorageUri { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public Dictionary<string, string> Attributes { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    public List<GoldenFigureSpec> Figures { get; set; } = [];

    /// <summary>
    /// ADR-0070 決定 3 / IADR-0356 (#1192) / [[IADR-0381]]: 原本が本文を持っていたかの宣言。
    /// **省略時は本文あり（`true`）。** `false` の case は `.body.md` を空にする。
    /// </summary>
    public bool HasBody { get; set; } = true;
}

/// <summary>抽出図 1 つと、その図に対する `IDiagramCoder` の応答の宣言。</summary>
internal sealed class GoldenFigureSpec
{
    public string FigureId { get; set; } = string.Empty;

    public string ImageContentType { get; set; } = string.Empty;

    public string? Caption { get; set; }

    /// <summary>省略時は figureId から決定的に作る。</summary>
    public string? ImageBase64 { get; set; }

    public bool Coded { get; set; }

    public string? Language { get; set; }

    public string? Code { get; set; }

    public string? Reason { get; set; }
}
