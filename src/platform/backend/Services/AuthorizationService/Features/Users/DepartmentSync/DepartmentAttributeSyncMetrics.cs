using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AuthorizationService.Features.Users.DepartmentSync;

// FR-05, FR-09, SC-17, 計画 ADR-0115 決定 3, [[IADR-0473]] (#1573 監査): 部門の同期の結末の計器。
//
// 🔴 **失敗が 0 でないことを外から見えるようにする。** 1 人の失敗で周期を止めない（他の人は直す）代わりに、
// 失敗は数えて出す —— ログだけだと、毎周期同じ人が失敗し続けても誰も気付かない。
// 属性 `department_sync.outcome` = corrected / cleared（#1609）/ skipped_changed / failed / not_found（周期ごとに件数を足す）。
public sealed class DepartmentAttributeSyncMetrics
{
    // 1 サービス 1 Meter の慣行に揃える（DocumentService の計器と同じく、サービス名の Meter に載せる）。
    public const string MeterName = "microservices-platform.authorization-service";
    public const string OutcomeCounterName = "department_sync.users.total";
    public const string CycleCounterName = "department_sync.cycles.total";
    public const string OutcomeTag = "department_sync.outcome";

    // ［2026-09-27 / #1609・計画 ADR-0116 決定 2］全利用者の列挙を読み切れなかった周期の数。
    // 🔴 **0 が正常。** 前進した周期は「部門グループから外れた利用者の属性を消していない」（原則 A で止めた）。
    // 属性 `department_sync.reason` = page_failed（ページの途中で失敗）/ truncated（上限で打ち切り）。
    public const string EnumerationIncompleteCounterName = "department_sync.enumeration_incomplete.total";
    public const string ReasonTag = "department_sync.reason";

    private readonly Counter<long> _outcomes;
    private readonly Counter<long> _cycles;
    private readonly Counter<long> _enumerationIncomplete;

    public DepartmentAttributeSyncMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _outcomes = meter.CreateCounter<long>(
            OutcomeCounterName, unit: "{user}",
            description: "部門の同期で書き込みを試みた利用者の結末。outcome = corrected / cleared / skipped_changed / failed / not_found。"
                       + "failed が 0 でなければ、その周期に直せなかった利用者が居る。");
        _cycles = meter.CreateCounter<long>(
            CycleCounterName, unit: "{cycle}",
            description: "部門の同期の周期数。outcome = completed / completed_with_failures / all_skipped_changed / aborted。"
                       + "all_skipped_changed が続くなら、書く直前の読み直しが毎回「変わった」と判定している（書けていない）。");
        _enumerationIncomplete = meter.CreateCounter<long>(
            EnumerationIncompleteCounterName, unit: "{cycle}",
            description: "部門の同期で全利用者の列挙を読み切れなかった周期の数。reason = page_failed / truncated。"
                       + "0 でなければ、その周期は部門グループから外れた利用者の属性 department を消していない。");
    }

    public void RecordEnumerationIncomplete(string reason)
        => _enumerationIncomplete.Add(1, new TagList { { ReasonTag, reason } });

    public void RecordUsers(string outcome, int count)
    {
        if (count > 0) _outcomes.Add(count, new TagList { { OutcomeTag, outcome } });
    }

    public void RecordCycle(string outcome) => _cycles.Add(1, new TagList { { OutcomeTag, outcome } });
}
