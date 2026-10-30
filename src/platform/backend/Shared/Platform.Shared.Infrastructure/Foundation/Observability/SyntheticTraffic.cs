using System.Security.Claims;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Shared.Infrastructure.Foundation.Observability;

// NFR-02, NFR-21, ADR-0044, ADR-0071, ADR-0072, ADR-0076 決定 4, [[IADR-0378]] (#1203):
// **合成監視（synthetic）のトラフィックを識別する標識の唯一の定義。**
//
// 計画 ADR-0076 決定 4 は「合成トラフィックは識別できる標識を持ち、ADR-0044 の LLM 費用計測と、
// FR-10 の利用状況・検索傾向（SC-10）の集計から除外する。**除外できない構成では合成監視を配備しない**」
// と定めた。標識の**形**は定めていないため、ここが実装の裁量である。
//
// 🔴 **判定材料は面ごとに違う。同じ 1 つの材料で両方を賄えない。**
//
//   外周（外部から到達し得る面。Platform.Bff の /bff/*、DashboardService の /dashboard/events）
//     → **検証済み JWT の主体だけを見る**（IsSyntheticPrincipal）。受信ヘッダは**一切見ない**。
//       見れば「外から印を付けて費用計上を免れる」経路ができ、**実トラフィックを費用集計から隠せる。**
//       利用者は他人の azp を名乗るトークンを発行できない（クライアント資格情報が要る）ため偽装できない。
//
//   内周（メッシュ内部だけの面。AiAnalysisService → LlmGateway）
//     → **外周が付けたヘッダを引き継ぐ**（IsSyntheticInternalRequest）。内部サービスは
//       ClusterIP ＋ NetworkPolicy 既定拒否 ＋ STRICT mTLS の内側にあり、これは [[IADR-0299]] が
//       `/internal/*` について受容した残余リスクと**同型・同じ境界**である。
//
// 🔴 **fail-closed**: 許可集合（`SyntheticMonitoring:Subjects`）が空なら**何も合成と見なさない**。
// 設定漏れが「全部が合成」へ倒れると、実利用がまるごと費用計上から消える。倒す向きを逆にする。
public static class SyntheticTraffic
{
    // 内周だけで信頼する伝播ヘッダ。**外周はこれを見ない**（上のコメント）。
    public const string HeaderName = "X-Synthetic-Traffic";

    // 付けるときの値。判定は「ヘッダが在り、値が空でないこと」で行う（値の表記ゆれで静かに外れないため）。
    public const string HeaderValue = "1";

    // NFR-02, ADR-0079 決定 1・2, [[IADR-0654]] (#2335): **SLO 評価用（60 分・LLM を呼ぶ）の合成**の値。
    // 合成の判定（値が空でないこと）はこの値でも真であり、費用計上・SC-10 の除外は従前どおり効く。
    // 内周で LLM を許すのは**この値のときだけ**である（`IsSloProbeInternalRequest`）。
    public const string SloHeaderValue = "slo";

    // 主体の照合に使う請求。**Keycloak のサービスアカウントは azp = clientId、
    // preferred_username = service-account-<clientId> を持つ**ため、どちらでも書けるようにする。
    // `sub` も許す（クライアント名が変わっても固定 ID で指せる）。
    private static readonly string[] SubjectClaimTypes =
    [
        "azp", "client_id", "clientId", "preferred_username", "sub",
        ClaimTypes.Name, ClaimTypes.NameIdentifier,
    ];

    // 外周の判定。**検証済みの主体だけを見る。**
    public static bool IsSyntheticPrincipal(ClaimsPrincipal? user, SyntheticMonitoringOptions options)
        => Classify(user, options) != SyntheticTrafficKind.None;

    // NFR-02, ADR-0079 決定 1・2, [[IADR-0654]] (#2335): 外周の分類（なし／常時の合成／SLO 評価の合成）。
    // **検証済みの主体だけを見る**（受信ヘッダは見ない）。`LlmSubjects` に在る主体は SLO（合成を含む）、
    // `Subjects` だけに在る主体は常時の合成。どちらの集合も空なら何も合成ではない（fail-closed）。
    public static SyntheticTrafficKind Classify(ClaimsPrincipal? user, SyntheticMonitoringOptions options)
    {
        if (user?.Identity?.IsAuthenticated != true) return SyntheticTrafficKind.None;

        var synthetic = false;
        foreach (var claimType in SubjectClaimTypes)
        {
            foreach (var claim in user.FindAll(claimType))
            {
                // fail-closed: LlmSubjects が空なら SLO は 1 件も存在しない（MatchesLlm が偽を返す）。
                if (options.MatchesLlm(claim.Value)) return SyntheticTrafficKind.SloProbe;
                if (options.Matches(claim.Value)) synthetic = true;
            }
        }

        return synthetic ? SyntheticTrafficKind.Synthetic : SyntheticTrafficKind.None;
    }

    // 内周の判定。**外周が付けたヘッダを引き継ぐだけ**であり、ここに認証の意味は無い。
    public static bool IsSyntheticInternalRequest(HttpRequest? request)
        => request is not null
           && request.Headers.TryGetValue(HeaderName, out var values)
           && !string.IsNullOrWhiteSpace(values.ToString());

    // NFR-02, ADR-0079 決定 1・2, [[IADR-0654]] (#2335): 内周で「SLO 評価の合成（LLM を呼んでよい）」か。
    // **値が `slo` のときだけ**（大文字小文字は問わない）。`1`・空・ヘッダなしは偽 —— 60 秒の合成は LLM を呼ばない。
    // 内周がヘッダを信頼する境界は `IsSyntheticInternalRequest` と同じである（[[IADR-0378]]）。
    public static bool IsSloProbeInternalRequest(HttpRequest? request)
        => request is not null
           && request.Headers.TryGetValue(HeaderName, out var values)
           && string.Equals(values.ToString().Trim(), SloHeaderValue, StringComparison.OrdinalIgnoreCase);

    // [[IADR-0654]] (#2335): 分類ごとの伝播（HTTP）。合成でなければ付けない。
    public static void PropagateTo(HttpRequestMessage request, SyntheticTrafficKind kind)
    {
        if (kind != SyntheticTrafficKind.None)
            request.Headers.TryAddWithoutValidation(HeaderName, HeaderValueFor(kind));
    }

    // 分類 → ヘッダの値（合成でなければ null）。
    public static string? HeaderValueFor(SyntheticTrafficKind kind) => kind switch
    {
        SyntheticTrafficKind.SloProbe => SloHeaderValue,
        SyntheticTrafficKind.Synthetic => HeaderValue,
        _ => null,
    };

    // 内周への伝播。**合成のときだけ付ける**（付けないことが既定であり、既存経路の形を変えない）。
    public static void PropagateTo(HttpRequestMessage request, bool isSynthetic)
    {
        if (isSynthetic)
            request.Headers.TryAddWithoutValidation(HeaderName, HeaderValue);
    }

    // NFR-02, ADR-0076 決定 4, ADR-0029, IADR-0378, IADR-0400 (#1255): east-west gRPC への伝播。
    //
    // 🔴 **同じクラスの多重定義にする。定義を 2 つにしない。** 標識の名前・値・「合成のときだけ付ける」
    // 規則は輸送に依らず 1 つであり、gRPC 用のヘルパを別クラスへ置くと、片方だけが直る事故の口になる。
    //
    // 🔴 **本文（proto）へ載せない**（IADR-0400 決定 3）。標識は「外周が付けたヘッダ」＝運搬の出所で
    // あって要求の意味ではない。本文へ `bool synthetic` を置くと**全 rpc の不変契約に番号つきで残り**、
    // 呼び出し元が「試験のため」に立てる典型的な誤用の口になる。
    //
    // 受け側は ASP.NET Core gRPC で `ServerCallContext.GetHttpContext().Request` が同じ `HttpRequest` に
    // なるため、上の `IsSyntheticInternalRequest` を**そのまま**呼べる。HTTP/2 はヘッダ名を小文字化する
    // が、`HttpRequest.Headers` の照合は大小文字無視なので `HeaderName` 定数は変えない。
    public static void PropagateTo(Metadata headers, bool isSynthetic)
    {
        if (isSynthetic)
            headers.Add(HeaderName, HeaderValue);
    }

    // 構成の束縛。**各サービスの Program.cs から 1 行で呼ぶ。**
    public static IServiceCollection AddSyntheticMonitoring(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SyntheticMonitoringOptions>(
            config.GetSection(SyntheticMonitoringOptions.SectionName));
        return services;
    }
}

// ADR-0076 決定 4, [[IADR-0378]] (#1203): 合成監視の構成。
//
// 🔴 **実装は既定値に「頻度」も「費用の上限」も持たない。** ADR-0076 §残るもの が
// 「合成監視の頻度・費用の上限は定めていない」と自認しており、**未定の数字を実装が埋めない**
// （#78 の向き。planning#... の裁定を待つ）。ここに在るのは「呼ぶかどうか」の可否だけである。
public sealed class SyntheticMonitoringOptions
{
    public const string SectionName = "SyntheticMonitoring";

    /// <summary>
    /// 合成監視の主体として扱う識別子の集合（clientId / preferred_username / sub のいずれか）。
    /// **空なら合成は 1 件も存在しない**（fail-closed）。
    /// </summary>
    public string[] Subjects { get; set; } = [];

    /// <summary>
    /// 合成トラフィックが LLM を実際に呼ぶことを許すか。
    /// 🔴 **既定は false。** ADR-0076 §残るもの が費用の上限を未定と残しているため、
    /// **上限が無いまま恒常的に費用を出すことを実装裁量で決めない**。false のとき
    /// AiAnalysisService は LLM を呼ばずに縮退し、費用は 0 になる。
    /// </summary>
    public bool AllowLlmEgress { get; set; }

    /// <summary>
    /// NFR-02, ADR-0079 決定 1・2, [[IADR-0654]] (#2335): **SLO 評価用（60 分・LLM を呼ぶ）の合成の主体**の集合。
    /// 外周（BFF）がここに在る主体の要求へ `X-Synthetic-Traffic: slo` を付け、内周（AiAnalysisService）は
    /// その要求だけ LLM を呼ぶ（**要求ごとの許可**。サービス全体に効く <see cref="AllowLlmEgress"/> とは別）。
    /// **空なら SLO の合成は 1 件も存在しない**（fail-closed）。ここに在る主体は合成でもある（費用計上・SC-10 から外す）。
    /// </summary>
    public string[] LlmSubjects { get; set; } = [];

    // 照合は序数・大文字小文字無視。**前後空白は落とす**（環境変数・ConfigMap 由来の値が
    // 空白を連れてくることがあり、静かに一致しなくなる）。
    public bool Matches(string? candidate) => MatchesAny(Subjects, candidate);

    // [[IADR-0654]] (#2335): SLO 評価の合成の主体か（照合の規則は Matches と同じ）。
    public bool MatchesLlm(string? candidate) => MatchesAny(LlmSubjects, candidate);

    private static bool MatchesAny(string[]? subjects, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || subjects is null) return false;
        foreach (var subject in subjects)
        {
            if (string.IsNullOrWhiteSpace(subject)) continue;
            if (string.Equals(subject.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

// NFR-02, ADR-0079 決定 1, [[IADR-0654]] (#2335): 外周の分類。
public enum SyntheticTrafficKind
{
    /// <summary>合成ではない（実利用）。</summary>
    None = 0,

    /// <summary>常時トラフィックの合成（60 秒・LLM を呼ばない）。</summary>
    Synthetic = 1,

    /// <summary>SLO 評価の合成（60 分・LLM を呼ぶ）。合成でもある。</summary>
    SloProbe = 2,
}
