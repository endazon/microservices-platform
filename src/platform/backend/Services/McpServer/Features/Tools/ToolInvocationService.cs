using System.Security.Claims;
using McpServer.Domain;
using McpServer.Infrastructure.Persistence;

namespace McpServer.Features.Tools;

// FR-16, UC-08: ツール呼び出しの唯一の経路。
//
// 🔴 **経路を 1 本にすることが統制の前提である。** ADR-0034 決定 9 の個人資料一律除外は
// 「探索系・検索系・文書取得系のすべてに適用する」と定められており、ツール種別ごとに経路が
// 分かれていると、どれか 1 本に除外を入れ忘れた瞬間に静かに破れる。**ツール名で分岐しない。**
//
// 手順（UC-08 基本フロー 3〜5）:
//   1. トークン → 登録済みクライアント（未登録・無効化は拒否）
//   2. 公開構成に載っているツールか（載っていなければ「不明なツール」＝存在秘匿）
//   3. 実行スコープを組む（サービスアカウントなら ExcludePrivateNote を立てる）
//   4. 下流サービスを呼ぶ
//   5. 個人資料の除外 → データ越境ポリシーの適用（本文 → 参照リンク縮退）
//   6. 監査ログ
public sealed class ToolInvocationService(
    McpSubjectResolver resolver,
    ToolCatalog catalog,
    IToolInvoker invoker,
    ServiceAccountDocumentFilter privateNoteFilter,
    EgressPolicy egressPolicy,
    ILogger<ToolInvocationService> logger)
{
    public async Task<ToolInvocationOutcome> InvokeAsync(
        ClaimsPrincipal principal, string toolName, string argumentsJson, CancellationToken ct)
    {
        var (subject, client, error) = await resolver.ResolveAsync(principal, ct);
        if (subject is null || client is null) return ToolInvocationOutcome.Rejected(error!);

        // ADR-0024 §決定「公開統制」: 既定は非公開。構成に無いツールは存在しないものとして扱う。
        // **「権限がありません」と返さない** —— 存在秘匿（11_mcp-server-integration §3）。
        var tool = catalog.Find(toolName);
        if (tool is null)
        {
            logger.LogInformation(
                "Rejected unknown MCP tool {Tool} for client {ClientId}",
                SanitizeForLog(toolName), subject.ClientId);
            return ToolInvocationOutcome.Rejected($"不明なツールです: {toolName}");
        }

        var scope = new ToolInvocationScope(
            subject.SubjectId,
            subject.Kind.ToString(),
            subject.Attributes,
            // 🔴 ADR-0034 決定 9: サービスアカウント実行なら個人資料を一律で対象外とする。
            ExcludePrivateNote: subject.IsServiceAccount,
            tool.Declaration.RequiredScope);

        var raw = await invoker.InvokeAsync(tool, scope, argumentsJson, ct);

        // 🔴 2 層目。要求側の制約を下流が無視しても、ここで落ちる（fail-closed）。
        var filtered = privateNoteFilter.Apply(subject, raw);

        // UC-08 基本フロー 5・代替フロー: 文書単位の送信可否判定と参照リンクへの縮退。
        var result = egressPolicy.Apply((EgressTier)client.EgressTier, filtered);

        // ADR-0024 §5: 全ツール呼び出しを監査ログに記録する
        // （誰が・どのエージェントで・どのツールを・どの引数概要で・結果件数）。
        logger.LogInformation(
            "MCP tool invoked: subject={SubjectId} kind={Kind} client={ClientId} tool={Tool} "
            + "argLength={ArgLength} returned={Returned} total={Total}",
            subject.SubjectId, subject.Kind, subject.ClientId, tool.PublishedName,
            argumentsJson.Length, result.Documents.Count, result.TotalCount);

        return ToolInvocationOutcome.Success(result);
    }

    // FR-16, UC-08 基本フロー 1: 呼び出し主体から見た公開ツール一覧。
    // 未登録・無効化されたクライアントには**空**を返す（一覧の時点で存在を見せない）。
    public async Task<IReadOnlyList<PublishedTool>> ListToolsAsync(
        ClaimsPrincipal principal, CancellationToken ct)
    {
        var (subject, _, _) = await resolver.ResolveAsync(principal, ct);
        return subject is null ? [] : catalog.PublishedTools;
    }

    /// <summary>ログ出力上限。要求由来の名前でログを溢れさせない。</summary>
    private const int MaxLoggedToolNameLength = 128;

    // CodeQL(cs/log-forging): ツール名は JSON-RPC の本文（`params.name`）由来であり、
    // 改行を含み得る。**行指向のログへ未加工で落とすと、偽の監査行を注入できる**
    // （本番の Program.cs は ClearProviders を呼んでおらず Console プロバイダが有効である）。
    // LlmRouter が同じ理由で置いている Sanitize と同型にする。長さも切る。
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var cleaned = new string(Array.ConvertAll(
            value.ToCharArray(), c => char.IsControl(c) ? '_' : c));
        return cleaned.Length <= MaxLoggedToolNameLength
            ? cleaned
            : cleaned[..MaxLoggedToolNameLength] + "…";
    }
}
