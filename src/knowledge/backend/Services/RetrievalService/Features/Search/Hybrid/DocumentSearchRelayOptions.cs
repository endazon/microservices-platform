using System.Security.Claims;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace RetrievalService.Features.Search.Hybrid;

// FR-19, NFR-09, 計画 ADR-0086 決定 1, ADR-0119 決定 3, [[IADR-0426]] 追記 1 (#1635):
// **gRPC `DocumentSearch/Search` の本文の利用者文脈（`UserContext`）を信じてよい呼び出し元の集合。**
//
// 面の門（`ServiceCaller`）は realm ロール `platform-service` だけを見る。そのロールは 11 のサービスアカウント
// （別プロジェクトのものを含む）が持つので、門だけで本文の `user_id` / 属性を信じると、どれもが任意の利用者
// （管理者を含む）を名乗り、その利用者のスコープ（個人資料の分岐を含む）でチャンクの**本文**を読める。
// ADR-0086 §結果が受け入れたのは「利用者の権限で動く中継者が正直であること」への依存であり、
// `DocumentSearch` でその中継者は AI 分析（`GrpcRagSearchTransport`）だけである。依存をそこへ狭める。
//
// 🔴 **既定は `aianalysis-service` だけ**（helm の aianalysis の `serviceToken.clientId`・compose の
//   `ServiceToken__ClientId`。`DocumentSearchRelayDeploymentWiringTests` が固定する）。
//   **構成したら既定を置き換える**（足し合わせない）。.NET の配列の束縛は初期値に構成値を**追記する**ので、
//   プロパティの既定を `["aianalysis-service"]` にすると外せなくなる。既定は null にし、`EffectiveClients` で解決する。
// 🔴 **空白だけの要素は捨てる。1 つも残らなければ誰も信じない**（fail-closed）。
// 🔴 **`MachinePrincipal` の「サービスアカウントの一覧を構成に持たない」とは向きが逆である。** あちらは一覧から
//   外すと統制を免れる（抜け道になる）。こちらは**許可**の集合で、外せば狭くなり、空なら誰も信じない。
// 🔴 **1 つの値（配列でない）の構成は起動時に止める**（`ThrowIfScalar`。`Program.cs` で束縛より前に呼ぶ。#1658）。
// 判定・既定の解決・構成の形の検査は共有の `TrustedUserContextRelay` が持つ（面ごとに写さない。#1658）。
public sealed class DocumentSearchRelayOptions
{
    public const string SectionName = "DocumentSearch";

    /// <summary>未構成のときの許可集合。AI 分析の s2s の client だけ。</summary>
    public static IReadOnlyList<string> DefaultTrustedUserContextClients { get; } = ["aianalysis-service"];

    /// <summary>
    /// 本文の利用者文脈を信じる呼び出し元のクライアント識別子（`azp`）。**null（未構成）なら既定の
    /// `aianalysis-service` だけ。** 構成すると既定を置き換える。
    /// </summary>
    public string[]? TrustedUserContextClients { get; set; }

    /// <summary>実際に使う許可集合（既定の解決・前後空白の除去・空要素の除去の後）。</summary>
    public IReadOnlyList<string> EffectiveClients =>
        TrustedUserContextRelay.Effective(TrustedUserContextClients, DefaultTrustedUserContextClients);

    /// <summary>
    /// 呼び出し元が本文の利用者文脈を運んでよいか（機械の主体 ∧ クライアント識別が**序数一致**で許可集合に在る。
    /// 接頭辞・大小文字の変種は別のクライアントである）。
    /// </summary>
    /// <remarks>
    /// 🔴 機械であることを併せて求める —— `azp` は人のトークンにも付く。
    /// 🔴 クライアント識別は `azp` を第一に見る。利用者名が `service-account-aianalysis-service` でも `azp` が別なら別のクライアントである。
    /// </remarks>
    public bool TrustsUserContextFrom(ClaimsPrincipal? caller) =>
        TrustedUserContextRelay.Trusts(caller, EffectiveClients);

    /// <summary>
    /// 1 つの値（配列でない。`DocumentSearch__TrustedUserContextClients=foo`）で構成されていたら起動時に止める。
    /// </summary>
    /// <remarks>
    /// 🔴 1 つの値は配列へ束縛されず、プロパティは null のまま**既定の `aianalysis-service` へ静かに戻る**（#1658）。
    /// 書き手は集合を置き換えたつもりで既定のまま動く。配列は `__0` / `__1` … で書く。
    /// </remarks>
    public static void ThrowIfScalar(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        TrustedUserContextRelay.ThrowIfScalar(configuration, SectionName, DefaultTrustedUserContextClients[0]);
}
