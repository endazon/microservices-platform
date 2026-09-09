using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;

namespace Knowledge.IntegrationTests.Fixtures;

// FR-14, NFR, ADR-0018, ADR-0027 手順 3, #1337:
// 派生器（`PipelineQueueOverride`）のふるまいを**コミットされた回帰試験**として固定する。
//
// 🔴 **fan-out の統合テスト本体は実ブローカと実 DB が要るため、ここでは実走できない。**
// だからこそ、器の側（宣言をどう派生させるか）は**Docker 不要で回る試験**で縛る ——
// 派生が静かに空振りすると、購読者は既定キュー（共有ブローカで滞留し得る側）を聴いたまま
// **緑になる**。#1337 が塞ごうとしている無音の穴と同型である。
[Trait("TestKind", "Unit")]
public class PipelineQueueOverrideTests
{
    // 段名・サービス名・input の形は正本 pipeline.json と同じにする（値は試験用）。
    private const string Sample = """
    {
      "version": 1,
      "steps": [
        { "name": "ingest", "service": "ingestion-service", "input": "DocumentUpdated", "enabled": true },
        { "name": "wiki-sync", "service": "wiki-service", "input": "DocumentUpdated", "enabled": true },
        { "name": "wiki-delete", "service": "wiki-service", "input": "DocumentDeleted", "enabled": true },
        { "name": "catalog", "service": "document-service", "input": "DocumentNormalized", "enabled": true }
      ]
    }
    """;

    private static JsonObject StepOf(string json, string name) =>
        JsonNode.Parse(json)!["steps"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(o => o["name"]!.GetValue<string>() == name);

    // FR-14, ADR-0027 手順 3: 指定した段の queue だけが差し替わる。
    [Fact]
    public void Derive_指定した段のqueueを差し替える()
    {
        var derived = PipelineQueueOverride.Derive(Sample, new Dictionary<string, string>
        {
            ["ingest"] = "docupd-abcd1234",
            ["wiki-sync"] = "docupd-abcd1234",
            ["wiki-delete"] = "docdel-abcd1234",
        });

        StepOf(derived, "ingest")["queue"]!.GetValue<string>().Should().Be("docupd-abcd1234");
        StepOf(derived, "wiki-sync")["queue"]!.GetValue<string>().Should().Be("docupd-abcd1234");
        StepOf(derived, "wiki-delete")["queue"]!.GetValue<string>().Should().Be("docdel-abcd1234");
    }

    // 🔴 差し替えなかった段は 1 バイトも変わらない。**正本の宣言をそのまま通すことが前提**であり
    //（規則 2 は登録される全段の宣言を要求する）、余計な queue を付けると別の段の購読先が動く。
    [Fact]
    public void Derive_指定しなかった段には手を触れない()
    {
        var derived = PipelineQueueOverride.Derive(Sample, new Dictionary<string, string>
        {
            ["ingest"] = "docupd-abcd1234",
        });

        var catalog = StepOf(derived, "catalog");
        catalog.ContainsKey("queue").Should().BeFalse("差し替えの対象でない段に queue を足さない");
        catalog["service"]!.GetValue<string>().Should().Be("document-service");
        catalog["input"]!.GetValue<string>().Should().Be("DocumentNormalized");
    }

    // FR-14, ADR-0027 手順 3: 🔴 **fail-closed**。段名が変わっていて差し替えが当たらなかったとき、
    // 黙って進めると購読者は既定キューを聴き、**テストが落ちた理由を取り違える**。
    [Fact]
    public void Derive_当たらない段名があれば止まる()
    {
        var act = () => PipelineQueueOverride.Derive(Sample, new Dictionary<string, string>
        {
            ["ingest"] = "q-1",
            ["wiki-sync-renamed"] = "q-1",
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*wiki-sync-renamed*");
    }

    // 🔴 同一サービスの 2 段へ同じ値を宣言すると、前置後のキュー名が衝突して
    // **2 つの購読が 1 本のキューへ潰れる**（fan-out の退行と症状が似る）。器の側で作り込ませない。
    [Fact]
    public void Derive_同一サービスの2段が同じキュー名になるなら止まる()
    {
        var act = () => PipelineQueueOverride.Derive(Sample, new Dictionary<string, string>
        {
            ["wiki-sync"] = "same-queue",
            ["wiki-delete"] = "same-queue",
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*wiki-service.same-queue*");
    }

    // 🔴 衝突相手は**差し替えなかった段の既定キュー**でもあり得る。実効キュー名は本番と同じ式
    //（宣言の queue が無ければ input）で導くため、この形も止まる。
    [Fact]
    public void Derive_差し替えなかった段の既定キューと衝突しても止まる()
    {
        var act = () => PipelineQueueOverride.Derive(Sample, new Dictionary<string, string>
        {
            // wiki-delete は差し替えないので既定キューは "DocumentDeleted" である。
            ["wiki-sync"] = "DocumentDeleted",
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*wiki-service.DocumentDeleted*");
    }

    [Fact]
    public void Derive_差し替える段が空なら引数として弾く()
    {
        var act = () => PipelineQueueOverride.Derive(Sample, new Dictionary<string, string>());

        act.Should().Throw<ArgumentException>();
    }

    // 🔴 派生元は**正本 pipeline.json** でなければならない（手で書き写すと本番の宣言が
    // 変わったときに黙って腐る）。正本に対して 3 段の差し替えが実際に当たることを見る。
    [Fact]
    public void WriteDerivedFixture_正本から3段を差し替えた一時ファイルを作る()
    {
        var path = PipelineQueueOverride.WriteDerivedFixture(new Dictionary<string, string>
        {
            ["ingest"] = "docupd-unit",
            ["wiki-sync"] = "docupd-unit",
            ["wiki-delete"] = "docdel-unit",
        }, "unit-pipeline");

        try
        {
            var derived = File.ReadAllText(path);
            StepOf(derived, "ingest")["queue"]!.GetValue<string>().Should().Be("docupd-unit");
            StepOf(derived, "wiki-sync")["queue"]!.GetValue<string>().Should().Be("docupd-unit");
            StepOf(derived, "wiki-delete")["queue"]!.GetValue<string>().Should().Be("docdel-unit");

            // 🔴 **正本は書き換わっていない。** 派生は一時ファイルに閉じる。
            var source = File.ReadAllText(RepoFile.Find(PipelineQueueOverride.SourceRelativePath));
            source.Should().NotContain("docupd-unit");
            JsonNode.Parse(source).Should().NotBeNull("正本が壊れていないこと");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // 正本の段名が変わったら、器は**空振りせずに止まる**（上の fail-closed が実際に効く相手は正本である）。
    [Fact]
    public void WriteDerivedFixture_正本に無い段名なら止まる()
    {
        var act = () => PipelineQueueOverride.WriteDerivedFixture(
            new Dictionary<string, string> { ["no-such-step"] = "q" }, "unit-pipeline");

        act.Should().Throw<InvalidOperationException>().WithMessage("*no-such-step*");
    }

    // 派生した宣言が JSON として読めること（ホストへ渡す前提。壊れていれば
    // AddPlatformPipelineConfig は黙って何もせず、宣言が 1 行も通らない）。
    [Fact]
    public void Derive_出力はJSONとして読める()
    {
        var derived = PipelineQueueOverride.Derive(Sample, new Dictionary<string, string> { ["ingest"] = "q-1" });

        var act = () => JsonSerializer.Deserialize<JsonObject>(derived);

        act.Should().NotThrow();
    }
}
