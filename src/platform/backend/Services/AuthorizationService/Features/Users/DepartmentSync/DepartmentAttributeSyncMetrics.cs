using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AuthorizationService.Features.Users.DepartmentSync;

// FR-05, FR-09, SC-17, 計画 ADR-0115 決定 3, [[IADR-0473]] (#1573 監査): 部門の同期の結末の計器。
//
// 🔴 **失敗が 0 でないことを外から見えるようにする。** 1 人の失敗で周期を止めない（他の人は直す）代わりに、
// 失敗は数えて出す —— ログだけだと、毎周期同じ人が失敗し続けても誰も気付かない。
// 属性 `department_sync.outcome` = corrected / skipped_changed / failed / not_found（周期ごとに件数を足す）。
public sealed class DepartmentAttributeSyncMetrics
{
    public const string MeterName = "microservices-platform.authorization-service.department-sync";
    public const string OutcomeCounterName = "department_sync.users.total";
    public const string CycleCounterName = "department_sync.cycles.total";
    public const string OutcomeTag = "department_sync.outcome";

    private readonly Counter<long> _outcomes;
    private readonly Counter<long> _cycles;

    public DepartmentAttributeSyncMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _outcomes = meter.CreateCounter<long>(
            OutcomeCounterName, unit: "{user}",
            description: "部門の同期で書き込みを試みた利用者の結末。outcome = corrected / skipped_changed / failed / not_found。"
                       + "failed が 0 でなければ、その周期に直せなかった利用者が居る。");
        _cycles = meter.CreateCounter<long>(
            CycleCounterName, unit: "{cycle}",
            description: "部門の同期の周期数。outcome = completed / completed_with_failures / aborted。");
    }

    public void RecordUsers(string outcome, int count)
    {
        if (count > 0) _outcomes.Add(count, new TagList { { OutcomeTag, outcome } });
    }

    public void RecordCycle(string outcome) => _cycles.Add(1, new TagList { { OutcomeTag, outcome } });
}
