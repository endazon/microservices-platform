using System.Collections.Concurrent;
using DocumentService.Api.Foundation.Ports;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace DocumentService.Api.Tests;

// FR-22: 通知の発火側の記録用スタブ。発火の有無・回数・ペイロード（件数・閾値・期限）を
// テストから直接見られるようにする。**自由文はポートの形として存在しない**ことも、
// この型のシグネチャがそのまま固定している。
public sealed class RecordingPrivateNoteNotifier : IPrivateNoteNotifier
{
    public record Sent(string Subject, string Kind, DateTimeOffset OccurredAt,
        int? Count, int? ThresholdPercent, DateTimeOffset? Deadline);

    public ConcurrentQueue<Sent> Notifications { get; } = new();

    public Task NotifyAsync(string subject, string kind, DateTimeOffset occurredAt,
        int? count = null, int? thresholdPercent = null, DateTimeOffset? deadline = null,
        CancellationToken ct = default)
    {
        Notifications.Enqueue(new Sent(subject, kind, occurredAt, count, thresholdPercent,
            deadline));
        return Task.CompletedTask;
    }

    public List<Sent> OfKind(string kind)
        => Notifications.Where(n => n.Kind == kind).ToList();
}

// FR-20, ADR-0037 決定 9: 監査ログの記録用スタブ。「誰が・いつ・何件」が残ること、
// およびタイトル・本文が**記録されない**ことをテストが直接検査するために用いる。
public sealed class RecordingAuditLogger : IAuditLogger
{
    public record Entry(string Action, string Subject, string Outcome, string? Detail);

    public ConcurrentQueue<Entry> Entries { get; } = new();

    public void Record(string action, string subject, string outcome, string? detail = null)
        => Entries.Enqueue(new Entry(action, subject, outcome, detail));

    public List<Entry> OfAction(string action)
        => Entries.Where(e => e.Action == action).ToList();
}
