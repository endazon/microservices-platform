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
// - 🔴 ［2026-10-30 / #2332］**ハーネスの同期の列挙も「待つ」ものとして使わない。** `Select<T>(ct)` は手元の記録が尽きると
//   取り消しの callback を登録して次の要素を待つ段に入り、届いた要素を lock を握ったまま返す。要素を見ている間に非活動の
//   トークンが取り消されると、callback は lock を待ち、こちらは列挙の破棄（登録の Dispose）で callback の完了を待って、
//   **互いに止まる**（MassTransit 8.4.1 `AsyncElementList.Select`。CI job 123724652810 の 30 分打ち切り）。
//   従前の注記は「その時点の記録の同期の列挙」と書いたが、待つ段を見落としていた。
//   いまは**既に取り消されたトークン**を渡す（`FindConsumed` / `AnyFault`）。手元の記録を返した直後に終わり、callback を
//   登録しない。待ちは自前の上限つきのポーリングだけである。
// - 🔴 **台帳の行が見えたことを待つ条件にしない。** consumer は台帳を保存した**後**に発行する（`SaveChanges…AndPublish…`）。
//   行が見えた時点で抜けると発行の前に検査へ進み、発行の有無の検査が flake し、発行が無いことの検査は偽の緑になる。
//   消費の記録はハーネスが consume の完了の後に積むので、これを待てば発行も済んでいる（consumer が出す Fault の発行も済んでいる）。
// - 完了を見た後に、失敗（記録の例外・`Fault<DocumentNormalized>` の発行）が無いこと、台帳の条件を確かめる。
internal static class DocumentNormalizedHarnessWait
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    // #2332: 既に取り消されたトークン。`Select` は手元の記録を返した直後に終わり、待つ段（callback の登録・`Monitor.Wait`）に入らない。
    private static readonly CancellationToken NoWait = new(canceled: true);

    /// <summary>文書 <paramref name="id"/> の消費の記録を、その時点の手元の記録だけから探す（待たない）。</summary>
    internal static IReceivedMessage<DocumentNormalized>? FindConsumed(ITestHarness harness, Guid id) =>
        harness.Consumed.Select<DocumentNormalized>(NoWait).FirstOrDefault(m => m.Context.Message.DocumentId == id);

    /// <summary><c>Fault&lt;DocumentNormalized&gt;</c> の発行が、その時点の手元の記録に在るか（待たない）。</summary>
    internal static bool AnyFault(ITestHarness harness) =>
        harness.Published.Select<Fault<DocumentNormalized>>(NoWait).Any();

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
        while ((consumed = FindConsumed(harness, id)) is null)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "consumer が上限までに消費を終えなかった");
            await Task.Delay(50, ct);
        }

        // 🔴 消費されたことと成功したことは別である（fault でも Consumed には載る）。
        consumed.Exception.Should().BeNull("consumer が落ちていれば台帳・発行の検査は無意味になる");
        AnyFault(harness).Should().BeFalse("consumer が落ちていれば台帳・発行の検査は無意味になる");

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        doc.Should().NotBeNull("消費が成功したなら台帳に文書が在る");
        if (applied is not null) applied(doc!).Should().BeTrue("消費の効果が台帳に当たっている");
        return doc!;
    }
}
