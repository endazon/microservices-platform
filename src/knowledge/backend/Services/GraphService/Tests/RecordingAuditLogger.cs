using Platform.Shared.Infrastructure.Foundation.Audit;

namespace GraphService.Tests;

// FR-18 (#2119): 監査ログの記録を取る差し替え（AuditLogger と同じ 4 引数）。
public sealed class RecordingAuditLogger : IAuditLogger
{
    public sealed record Entry(string Action, string Subject, string Outcome, string? Detail);

    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> Entries
    {
        get { lock (_entries) return [.. _entries]; }
    }

    public void Record(string action, string subject, string outcome, string? detail = null)
    {
        lock (_entries) _entries.Add(new Entry(action, subject, outcome, detail));
    }
}
