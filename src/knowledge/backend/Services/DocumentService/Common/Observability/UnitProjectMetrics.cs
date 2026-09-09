using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace DocumentService.Common.Observability;

// FR-05, FR-09, FR-16, SC-10, SC-12, ADR-0036, ADR-0062, ADR-0085 決定 4, [[IADR-0420]] (#1233):
// **ユニットの主体が保存した文書のうち `project` を持たない件数**（ナレッジ健全性の 8 指標目）。
//
// **0 が正常である。** `IngestTagMetrics` と同じ読み方であり（同じ表に並ぶ他の 6 指標は
// 「発生してよい事象の量」を測る）、**発生してはならない事象の件数**である ——
// ユニット側の保存経路は `project` を無条件で付与する（AST は `IADR-0293`）ため、
// **1 件でも計上されればその経路を通らない書き込みが起きている。**
//
// 🔴 **これは統制ではなく計器である**（`ADR-0085` 決定 4）。計上されても保存は止まらない。
// 止めるには基盤側で `project` を必須化するしかなく、計画は決定 1 でそれを**しない**と定めた。
// **「観測手段を置いた」を「穴を塞いだ」と読ませない。**
//
// 🔴 **測るのは書き手の側である。** 「`project` を持たない文書の総数」は指標にしない ——
// 母数がほぼ全件（実データ 2,368 件は `confidentiality` 以外の属性を持たない）で張り付き、
// 付与漏れの変化を映さない（`ADR-0085` 決定 4）。
//
// **SC-10 の画面へは出さない。** 「ナレッジ健全性」節は [[IADR-0119]] により節ごと着手保留であり、
// ここに 1 指標だけ差し込むと保留の線引きが壊れる（`IngestTagMetrics` と同じ判断）。
// **裁定が求めたのは「0 でない値が検出になる」ことであり、Grafana で観測できれば成立する。**
public sealed class UnitProjectMetrics
{
    // Meter 名はサービス名と一致させる（OTLP の収集対象。`IngestTagMetrics` と同じ器）。
    public const string MeterName = "microservices-platform.document-service";

    public const string MissingCounterName = "documents.unit_project_missing.total";

    /// <summary>計上した主体のクライアント識別子（`azp` の生値）。realm の機密クライアント数に閉じる。</summary>
    public const string ClientIdTag = "documents.client_id";

    /// <summary>保存の操作。<see cref="OperationCreate"/> / <see cref="OperationUpdate"/> / <see cref="OperationUpdateMetadata"/>。</summary>
    public const string OperationTag = "documents.operation";

    public const string OperationCreate = "create";
    public const string OperationUpdate = "update";
    public const string OperationUpdateMetadata = "update_metadata";

    /// <summary>クライアント識別子を取り出せなかったときの属性値（基数は 1 に閉じる）。</summary>
    public const string UnknownClientId = "unknown";

    private readonly Counter<long> _missing;

    public UnitProjectMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _missing = meter.CreateCounter<long>(
            MissingCounterName,
            unit: "{document}",
            description: "ユニットの主体（サービスアカウント）が保存した文書のうち、保存後の属性に "
                       + "project を持たない件数（0 が正常）。");
    }

    /// <summary>
    /// **保存が成立した後**に呼ぶ。無人主体の保存で、保存後の属性に <c>project</c> が無いときだけ
    /// 1 件計上する。
    ///
    /// 🔴 **判定を呼び出し側へ散らさない。** 3 つの保存経路（登録・編集・メタデータ更新）が同じ
    /// 母集合を持つため、「誰が」と「何が欠けているか」の判定はここ 1 か所に閉じる ——
    /// 散らすと 1 箇所だけ条件が変わって**指標の母集合が経路ごとに違うものになる**
    /// （`DocumentEndpoints.PublishUpdatedAsync` が共有先の解決を呼び出し側へ渡さないのと同じ理由）。
    ///
    /// 🔴 **文書 ID・題名は載せない**（`ADR-0085` 決定 4 が「件数のみ・文書名へのドリルダウンを設けない」
    /// を本指標にも適用すると定めている）。名前はログへ、件数はカウンタへ。
    /// </summary>
    public void RecordIfUnitSubjectSavedWithoutProject(
        ClaimsPrincipal? user, IReadOnlyDictionary<string, string> savedAttributes, string operation)
    {
        if (!MachinePrincipal.IsMachine(user)) return;
        if (HasProject(savedAttributes)) return;

        _missing.Add(1, new TagList
        {
            { ClientIdTag, MachinePrincipal.ClientIdOf(user) ?? UnknownClientId },
            { OperationTag, operation },
        });
    }

    // **空文字・空白だけの値は「持たない」と読む。** キーの有無だけで見ると、
    // `project=""` を送るだけで計上を免れられる（統制ではなく計器だが、抜け道の形は同じである）。
    private static bool HasProject(IReadOnlyDictionary<string, string> attributes)
        => attributes.TryGetValue(RestrictedProject.DocumentKey, out var value)
           && !string.IsNullOrWhiteSpace(value);
}
