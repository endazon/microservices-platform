using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentService.Tests.Features.Documents.Catalog;

// NFR, #2306: `DocumentNormalized` を in-memory ハーネスへ流した試験の待ち合わせ。
//
// **消費の効果（台帳の文書が条件を満たす）か失敗（`Fault<DocumentNormalized>` の発行）のどちらかを、上限まで待つ。**
// 🔴 `harness.Consumed.Any<T>()` を待ちに使わない。ハーネスの**非活動タイムアウト**が切れると「まだ来ていない」を
// 「来ない」として False を返す。機械が混んで配送・消費が窓を超えると、実装が正しいのに落ちる
// （CI run 40902248997 の `NormalizedAssetLedgerTests.資産の無い文書は空配列になる`）。窓は延ばさず、待つ条件そのものを
// 効果か失敗にする。条件が成り立てば即座に抜けるので、固定の待ちではない（上限は落ちるときの時間だけを決める）。
internal static class DocumentNormalizedHarnessWait
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 文書 <paramref name="id"/> が台帳に在り、<paramref name="applied"/>（省略時は在るだけ）を満たすまで待って返す。
    /// 待つ間に consumer の失敗（Fault）が発行されたら、その時点で落とす。
    /// </summary>
    public static async Task<Document> UntilDocumentAsync(IServiceProvider provider, ITestHarness harness, Guid id,
        Func<Document, bool>? applied = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow + Limit;
        while (true)
        {
            // 🔴 消費されたことと成功したことは別である（fault でも Consumed には載る）。失敗は待たずに見る。
            harness.Published.Select<Fault<DocumentNormalized>>(ct).Any().Should()
                .BeFalse("consumer が落ちていれば台帳の検査は無意味になる");
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
                var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
                if (doc is not null && (applied is null || applied(doc))) return doc;
            }
            DateTime.UtcNow.Should().BeBefore(deadline, "consumer が上限までに台帳へ書かなかった");
            await Task.Delay(50, ct);
        }
    }
}
