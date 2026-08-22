using System.Text.Json;
using McpServer.Api.Foundation.Contracts;

namespace McpServer.Api.Foundation.Services;

// FR-16, ADR-0024 §2・§5: 宣言的公開構成（Git 管理・GitOps 適用）の読み込み。
//
// 🔴 **検証に落ちた構成は適用しない**（ADR-0024 §5「検証を通らない構成は適用不可」）。
// 起動時に例外で落とす —— 壊れた構成で起動して「公開されているつもりの公開されていない」
// 状態になるより、起動しないほうが気づける。
public sealed class ToolPublicationConfigLoader(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public const string PathKey = "Mcp:PublicationConfigPath";

    public ToolPublicationConfig Load()
    {
        var path = configuration[PathKey];
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(AppContext.BaseDirectory, "Configuration", "mcp-publication.json");

        if (!File.Exists(path))
        {
            // 構成ファイルが無い＝公開ツール 0 件（既定は非公開）。**空で起動してよい**。
            // ここで落とすと、まだツールを公開しない環境でサービスが起動できなくなる。
            return new ToolPublicationConfig("empty", []);
        }

        var config = JsonSerializer.Deserialize<ToolPublicationConfig>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"MCP 公開構成を読み込めません: {path}");

        var errors = ToolPublicationConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"MCP 公開構成のスキーマ検証に失敗しました（{path}）:{Environment.NewLine}"
                + string.Join(Environment.NewLine, errors));
        }
        return config;
    }
}
