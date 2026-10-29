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
// **文書 id の `DocumentNormalized` の消費が完了する（consumer の処理が戻る）まで、上限つきで待つ。**
// - 🔴 `harness.Consumed.Any<T>()`（非同期の待ち）を使わない。ハーネスの**非活動タイムアウト**が切れると「まだ来ていない」を
//   「来ない」として False を返す。機械が混んで配送・消費が窓を超えると、実装が正しいのに落ちる
//   （CI run 40902248997 の `NormalizedAssetLedgerTests.資産の無い文書は空配列になる`）。
//   ここでは**その時点の記録の同期の列挙**（`Consumed.Select<T>(ct)`）を自分の上限でポーリングするので、窓に依存しない。
// - 🔴 **台帳の行が見えたことを待つ条件にしない。** consumer は台帳を保存した**後**に発行する（`SaveChanges…AndPublish…`）。
//   行が見えた時点で抜けると発行の前に検査へ進み、発行の有無の検査が flake し、発行が無いことの検査は偽の緑になる。
//   消費の記録はハーネスが consume の完了の後に積むので、これを待てば発行も済んでいる。
// - 完了を見た後に、失敗（記録の例外・`Fault<DocumentNormalized>` の発行）が無いこと、台帳の条件を確かめる。
internal static class DocumentNormalizedHarnessWait
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 文書 <paramref name="id"/> の消費の完了を待ち、失敗が無く、台帳の文書が <paramref name="applied"/>（省略時は在るだけ）を
    /// 満たすことを確かめて返す。
    /// </summary>
    public static async Task<Document> UntilConsumedAsync(IServiceProvider provider, ITestHarness harness, Guid id,
        Func<Document, bool>? applied = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow + Limit;
        IReceivedMessage<DocumentNormalized>? consumed;
        while ((consumed = harness.Consumed.Select<DocumentNormalized>(ct)
                   .FirstOrDefault(m => m.Context.Message.DocumentId == id)) is null)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "consumer が上限までに消費を終えなかった");
            await Task.Delay(50, ct);
        }

        // 🔴 消費されたことと成功したことは別である（fault でも Consumed には載る）。
        consumed.Exception.Should().BeNull("consumer が落ちていれば台帳・発行の検査は無意味になる");
        harness.Published.Select<Fault<DocumentNormalized>>(ct).Any().Should()
            .BeFalse("consumer が落ちていれば台帳・発行の検査は無意味になる");

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        doc.Should().NotBeNull("消費が成功したなら台帳に文書が在る");
        if (applied is not null) applied(doc!).Should().BeTrue("消費の効果が台帳に当たっている");
        return doc!;
    }
}
