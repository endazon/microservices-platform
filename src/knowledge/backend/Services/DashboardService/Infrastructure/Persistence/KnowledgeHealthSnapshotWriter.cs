using DashboardService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace DashboardService.Infrastructure.Persistence;

// FR-10, FR-17, FR-18, NFR-16, SC-10 (#1895): 指標 1 つ分のスナップショット置換を**一括で**書く。
//
// 🔴 **行数に比例する往復を作らない。** 従前の受け口は、既存行を変更追跡つきで読み、`RemoveRange` で 1 行ずつ
// DELETE し、`AddRange` で 1 行ずつ INSERT していた。38,703 件の孤立文書で呼び出し元の期限（5 秒）を超え、
// 期限で取り消されてロールバックしていた（毎周期、値が更新されない）。ここでは件数によらず次の文だけを流す:
//   1. `DELETE ... WHERE Indicator = @p`（`ExecuteDeleteAsync`。行を読み込まない）
//   2. `INSERT ... SELECT FROM unnest(@ids, @subjectKeys, @docScopes, @dimensions)`（配列 4 本を 1 文で渡す）
//   3. しきい値の 1 行を読み、追加・更新・削除のいずれか 1 文（`SaveChangesAsync`）
//
// 🔴 **3 つを 1 つのトランザクションに入れる（原子性）。** 読み手（閲覧の GET）は READ COMMITTED で読むため、
// コミット前の「消しただけ」「半分入れた」状態は見えない。旧い集合か新しい集合のどちらかだけが見える。
// 期限切れ・取り消し・例外ではコミットせずに破棄する（`await using` の Dispose がロールバックする）ので、
// 置換の途中で止まっても旧い集合がそのまま残る。
//
// ★ COPY ではなく `unnest` を採る: EF の接続・トランザクション・コマンドの傍受（試験の数え上げ）にそのまま乗る。
// 4 万件の実測は作業仕様書（20261011_1895_knowledge-health-bulk-replace）に置いた。
//
// ★ InMemory（DashboardService.Tests）は `ExecuteDeleteAsync`・生 SQL・トランザクションを持たないので、
// 関係 DB でないときだけ従前の変更追跡の経路で書く（`GraphSyncTransaction` と同じ分岐）。
// **関係 DB の経路は Knowledge.IntegrationTests の実 PostgreSQL で測る。**
internal static class KnowledgeHealthSnapshotWriter
{
    public static async Task ReplaceAsync(
        DashboardDbContext db,
        string indicator,
        IReadOnlyList<KnowledgeHealthObservation> observations,
        int? thresholdDays,
        DateTimeOffset observedAt,
        CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            await ReplaceTrackedAsync(db, indicator, observations, thresholdDays, observedAt, ct);
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.KnowledgeHealthObservations
            .Where(o => o.Indicator == indicator)
            .ExecuteDeleteAsync(ct);

        if (observations.Count > 0)
            await InsertBulkAsync(db, observations, ct);

        await ApplyThresholdAsync(db, indicator, thresholdDays, observedAt, ct);
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
    }

    // 観測値を 1 文で挿入する。列名は EF のモデルから引く（移行で列名が変わったときに SQL だけ古く残らない）。
    private static async Task InsertBulkAsync(
        DashboardDbContext db, IReadOnlyList<KnowledgeHealthObservation> observations, CancellationToken ct)
    {
        var entity = db.Model.FindEntityType(typeof(KnowledgeHealthObservation))
            ?? throw new InvalidOperationException("KnowledgeHealthObservation is not mapped.");
        var tableName = entity.GetTableName()
            ?? throw new InvalidOperationException("KnowledgeHealthObservation has no table.");
        var schema = entity.GetSchema();
        var store = StoreObjectIdentifier.Table(tableName, schema);
        var sql = db.GetService<ISqlGenerationHelper>();

        string Column(string property) => sql.DelimitIdentifier(
            entity.FindProperty(property)?.GetColumnName(store)
            ?? throw new InvalidOperationException($"KnowledgeHealthObservation.{property} has no column."));

        var count = observations.Count;
        var ids = new Guid[count];
        var indicators = new string[count];
        var subjectKeys = new string[count];
        var docScopes = new string?[count];
        var dimensions = new string?[count];
        var observedAts = new DateTimeOffset[count];
        for (var i = 0; i < count; i++)
        {
            var o = observations[i];
            ids[i] = o.Id;
            indicators[i] = o.Indicator;
            subjectKeys[i] = o.SubjectKey;
            docScopes[i] = o.DocScope;
            dimensions[i] = o.Dimension;
            observedAts[i] = o.ObservedAt;
        }

        var insert =
            $"INSERT INTO {sql.DelimitIdentifier(tableName, schema)} " +
            $"({Column(nameof(KnowledgeHealthObservation.Id))}, " +
            $"{Column(nameof(KnowledgeHealthObservation.Indicator))}, " +
            $"{Column(nameof(KnowledgeHealthObservation.SubjectKey))}, " +
            $"{Column(nameof(KnowledgeHealthObservation.DocScope))}, " +
            $"{Column(nameof(KnowledgeHealthObservation.Dimension))}, " +
            $"{Column(nameof(KnowledgeHealthObservation.ObservedAt))}) " +
            "SELECT * FROM unnest(@ids, @indicators, @subject_keys, @doc_scopes, @dimensions, @observed_ats)";

        await db.Database.ExecuteSqlRawAsync(
            insert,
            [
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids },
                new NpgsqlParameter("indicators", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = indicators },
                new NpgsqlParameter("subject_keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = subjectKeys },
                new NpgsqlParameter("doc_scopes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = docScopes },
                new NpgsqlParameter("dimensions", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = dimensions },
                new NpgsqlParameter("observed_ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = observedAts },
            ],
            ct);
    }

    // InMemory のための従前の経路（変更追跡）。**本番（PostgreSQL）では通らない。**
    private static async Task ReplaceTrackedAsync(
        DashboardDbContext db,
        string indicator,
        IReadOnlyList<KnowledgeHealthObservation> observations,
        int? thresholdDays,
        DateTimeOffset observedAt,
        CancellationToken ct)
    {
        var stale = await db.KnowledgeHealthObservations
            .Where(o => o.Indicator == indicator)
            .ToListAsync(ct);
        db.KnowledgeHealthObservations.RemoveRange(stale);
        db.KnowledgeHealthObservations.AddRange(observations);
        await ApplyThresholdAsync(db, indicator, thresholdDays, observedAt, ct);
        await db.SaveChangesAsync(ct);
    }

    // planning#494 決定 3 (#1186): 現在のしきい値も**スナップショットとして置き換える**。
    // 🔴 **添えられていなければ行を消す。** 残すと、生産者がしきい値の要らない指標へ
    // 変わった後も古い日数が画面に出続ける（観測値を全量置換するのと同じ理由）。
    private static async Task ApplyThresholdAsync(
        DashboardDbContext db, string indicator, int? thresholdDays, DateTimeOffset observedAt,
        CancellationToken ct)
    {
        var threshold = await db.KnowledgeHealthIndicatorThresholds
            .FirstOrDefaultAsync(t => t.Indicator == indicator, ct);
        if (thresholdDays is { } days)
        {
            if (threshold is null)
                db.KnowledgeHealthIndicatorThresholds.Add(
                    KnowledgeHealthIndicatorThreshold.Create(indicator, days, observedAt));
            else
                threshold.Update(days, observedAt);
        }
        else if (threshold is not null)
        {
            db.KnowledgeHealthIndicatorThresholds.Remove(threshold);
        }
    }
}
