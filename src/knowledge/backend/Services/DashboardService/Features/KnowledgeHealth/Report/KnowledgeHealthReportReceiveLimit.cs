using System.Globalization;
using Grpc.AspNetCore.Server;
using Microsoft.Extensions.Options;

namespace DashboardService.Features.KnowledgeHealth.Report;

// FR-10, FR-17, FR-18, NFR-16, ADR-0029, ADR-0075, [[IADR-0542]] 決定 1 (#1907): 観測値の受け口（H-1。`KnowledgeHealthReport/Report`）の
// **受信上限**。grpc-dotnet の既定 4 MB では、1 要求に指標 1 つ分の全量を載せる H-1 が約 9.5 万件で
// `ResourceExhausted` になり、毎周期失敗して閲覧が古いまま残る（#1895 と同じ症状）。
//
// 🔴 **この rpc（このサービス）だけに効かせる**（`GrpcServiceOptions<KnowledgeHealthReportGrpcService>`）。
//   サーバ全体の `GrpcServiceOptions` を上げると、introspection など他の面の 1 要求あたりのメモリ上限まで上がる。
// 🔴 **0 以下・数値でない値は起動時に落とす**（#1899 の D-1 と同じ姿勢）。黙って既定へ戻すと、
//   構成の誤りが「健全性の閲覧が古いまま」として再発し、原因が構成だと読めない。
// 🔴 **分割（世代つき書き込み）・client streaming は採らない**（[[IADR-0542]] 決定 3）。
//   全量置換の原子性（#1895 の 1 トランザクション）をそのまま保つための選択である。
public static class KnowledgeHealthReportReceiveLimit
{
    /// <summary>構成キー（環境変数では `Grpc__KnowledgeHealthReportMaxReceiveMessageSize`）。単位はバイト。</summary>
    public const string ConfigKey = "Grpc:KnowledgeHealthReportMaxReceiveMessageSize";

    /// <summary>
    /// 既定 16 MiB。観測値 1 件の符号化は形により約 40〜95 バイトで、最も重い形（解決できないリンク：
    /// 65 文字の鍵・軸・個人資料の印）でも 15 万件が約 14 MB に収まる。軽い形（文書 ID だけ）なら約 40 万件まで入る。
    /// 送信側（graph）は 12 MiB（本既定の 75%）を超えたら警告を出す（`GrpcKnowledgeHealthReporter.PayloadWarnBytes`）。
    /// </summary>
    public const int DefaultMaxReceiveMessageSize = 16 * 1024 * 1024;

    // 受け口（KnowledgeHealthReportGrpcService）にだけ受信上限を与える。
    // 🔴 **`AddGrpc()` を先に呼ぶ。** grpc-dotnet はサービス別の既定（全体の値の写し）を `AddGrpc()` が登録する
    //   `IConfigureOptions` で入れるので、それより前に登録した Configure は全体の値（4 MB）で上書きされる。
    // 構成は **DI の `IConfiguration` から遅延で読む**（`WebApplicationFactory` が差し込む構成も見える）。
    // `ValidateOnStart` で起動時に 1 度解決し、不正値をその場で落とす。
    public static IServiceCollection AddKnowledgeHealthReportReceiveLimit(this IServiceCollection services)
    {
        services.AddGrpc();
        services.AddOptions<GrpcServiceOptions<KnowledgeHealthReportGrpcService>>()
            .Configure<IConfiguration>((options, config) => options.MaxReceiveMessageSize = Read(config))
            .ValidateOnStart();
        return services;
    }

    internal static int Read(IConfiguration config)
    {
        var raw = config[ConfigKey];
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultMaxReceiveMessageSize;
        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes > 0)
            return bytes;
        throw new InvalidOperationException($"{ConfigKey} は正の整数（バイト）で指定してください: '{raw}'");
    }
}
