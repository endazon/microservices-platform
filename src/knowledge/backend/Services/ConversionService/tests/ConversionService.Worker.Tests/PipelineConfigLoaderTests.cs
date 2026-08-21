using AwesomeAssertions;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ConversionService.Worker.Tests;

// FR-15, #146: パイプライン宣言ローダは 2 形式を読み込めることを検証する。
//   1) Helm ConfigMap の {"Pipeline": {...}} オーバレイ
//   2) compose がマウントする「生の pipeline.json」（"Pipeline" セクションへ包んで読む）
// これにより正の pipeline.json を複製せず compose の BFF へ供給でき、ドリフト突合が正しく働く。
public class PipelineConfigLoaderTests
{
    private const string RawPipeline = """
        { "version": 1, "steps": [
          { "name": "convert", "service": "conversion-service",
            "consumer": "ConversionService.Worker.Composable.Steps.RawDocumentFetchedConsumer",
            "input": "RawDocumentFetched", "outputs": ["DocumentNormalized"], "enabled": true } ] }
        """;

    private static PipelineOptions Load(string fileContent)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipeline-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, fileContent);
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pipeline:ConfigPath"] = path
            });
            builder.AddPlatformPipelineConfig();
            return builder.Configuration.GetPlatformPipeline();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Loads_raw_pipeline_json_by_wrapping_into_Pipeline_section()
    {
        var pipeline = Load(RawPipeline);

        pipeline.Steps.Should().ContainSingle(s => s.Name == "convert")
            .Which.Consumer.Should().Be(
                "ConversionService.Worker.Composable.Steps.RawDocumentFetchedConsumer");
    }

    [Fact]
    public void Loads_wrapped_overlay_as_before()
    {
        var wrapped = $$"""{ "Pipeline": {{RawPipeline}} }""";

        var pipeline = Load(wrapped);

        pipeline.Steps.Should().ContainSingle(s => s.Name == "convert");
    }
}
