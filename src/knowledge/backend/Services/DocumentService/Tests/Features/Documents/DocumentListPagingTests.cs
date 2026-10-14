using System.Buffers.Text;
using System.Text;
using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.Documents;
using DocumentService.Infrastructure.Persistence;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Tests.Features.Documents;

// FR-06, UC-03, SC-05, NFR-16, ADR-0029, [[IADR-0009]], [[IADR-0537]] (#1923・#1897):
// gRPC `ListDocuments` のページング（`DocumentReadUseCase.ListPageAsync` と続きの印 `DocumentListCursor`）。
//
// 1 試験 1 台帳（InMemory の名前を試験ごとに変える）。ページの境界・並びは台帳の全件に依るので、他の試験の文書を混ぜない。
// 🔴 **読めない文書の扱いは陽性対照と対で置く**（同じ台帳で、所有者には返ることを先に確かめる）。
public class DocumentListPagingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static DocumentDbContext NewLedger() => new(
        new DbContextOptionsBuilder<DocumentDbContext>().UseInMemoryDatabase($"list-paging-{Guid.NewGuid():N}").Options);

    private static DocumentReadUseCase Reads(DocumentDbContext db)
        => new(db, new DocumentReadAccess(new StubDocumentReadScopeSource(), new StubContentAbacGate()));

    private static readonly DocumentReadPrincipal Machine = DocumentReadPrincipal.RelayedUser("service-account-bff");

    private static Dictionary<string, string> Org() => new()
    {
        ["confidentiality"] = "internal",
        ["doc_scope"] = "organization",
    };

    private static Dictionary<string, string> PrivateNote(string owner) => new()
    {
        ["confidentiality"] = "restricted",
        ["doc_scope"] = "private-note",
        ["owner"] = owner,
    };

    // 更新時刻を与えて台帳へ入れる（`UpdatedAt` は台帳の初期化子が決めるので、試験では値を直に置く）。
    private static async Task<Document> SeedAsync(DocumentDbContext db, string title, DateTimeOffset updatedAt,
        Dictionary<string, string>? attributes = null, Guid? id = null)
    {
        var doc = Document.Create(title, originalUri: null, contentType: null, attributes: attributes ?? Org());
        db.Documents.Add(doc);
        if (id is { } fixedId)
            db.Entry(doc).Property(d => d.Id).CurrentValue = fixedId;
        db.Entry(doc).Property(d => d.UpdatedAt).CurrentValue = updatedAt;
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();
        return doc;
    }

    // 印を辿って全ページを読む。各ページの件数も返す（境界の検査に使う）。
    private static async Task<(List<Guid> Ids, List<int> PageSizes)> ReadAllAsync(
        DocumentReadUseCase reads, DocumentReadPrincipal principal, int pageSize)
    {
        var ids = new List<Guid>();
        var sizes = new List<int>();
        DocumentListCursor? after = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var (page, next) = await reads.ListPageAsync(principal, pageSize, after, Ct);
            ids.AddRange(page.Select(d => d.Id));
            sizes.Add(page.Count);
            if (next is null) return (ids, sizes);
            DocumentListCursor.TryDecode(next, out var decoded).Should().BeTrue();
            after = decoded;
        }
        throw new InvalidOperationException("ページの続きが終わらない");
    }

    // ── ページの境界 ─────────────────────────────────────────────

    // 5 件を 2 件ずつ辿ると 2・2・1 件。重複も抜けも無く、最後のページの続きの印は無い。
    [Fact]
    public async Task 印を辿ると重複も抜けも無く全件を1回ずつ返し_最後のページで印が無くなる()
    {
        await using var db = NewLedger();
        var seeded = new List<Document>();
        for (var i = 0; i < 5; i++)
            seeded.Add(await SeedAsync(db, $"doc-{i}", T0.AddMinutes(i)));

        var (ids, sizes) = await ReadAllAsync(Reads(db), Machine, pageSize: 2);

        sizes.Should().Equal(2, 2, 1);
        ids.Should().Equal(seeded.OrderByDescending(d => d.UpdatedAt).Select(d => d.Id),
            "更新日時の降順で、ちょうど 1 回ずつ返る");
    }

    // 件数がページの大きさの倍数ちょうどでも、空のページを 1 枚余計に返さない（`pageSize + 1` 件目で続きを判定する）。
    [Fact]
    public async Task 件数がページの大きさの倍数ちょうどなら最後のページで印が無くなる()
    {
        await using var db = NewLedger();
        for (var i = 0; i < 4; i++)
            await SeedAsync(db, $"doc-{i}", T0.AddMinutes(i));

        var (ids, sizes) = await ReadAllAsync(Reads(db), Machine, pageSize: 2);

        sizes.Should().Equal(2, 2);
        ids.Should().HaveCount(4);
    }

    // ── 安定順序 ───────────────────────────────────────────────

    // 更新日時が同じ文書は ID の降順で並ぶ（全順序）。ページの境界が同時刻の文書の間に落ちても抜け・重複が無い。
    [Fact]
    public async Task 更新日時が同じ文書はIDの降順に並び_境界が同時刻の間に落ちても抜けない()
    {
        await using var db = NewLedger();
        var ids = Enumerable.Range(1, 5)
            .Select(i => Guid.Parse($"00000000-0000-0000-0000-00000000000{i}"))
            .ToList();
        foreach (var id in ids)
            await SeedAsync(db, $"same-{id}", T0, id: id);
        var newer = await SeedAsync(db, "newer", T0.AddSeconds(1));

        var (read, _) = await ReadAllAsync(Reads(db), Machine, pageSize: 2);

        read.Should().Equal([newer.Id, .. ids.OrderByDescending(g => g)]);
    }

    // ── 読めない文書（存在秘匿。IADR-0009） ───────────────────────────

    // 他人の個人資料はページにも入らず、ページは読める文書で満たされる。
    // 🔴 **続きの印は読めない文書を指さない** —— 境界の直後に読めない文書が在っても、印は返した最後の文書の ID と時刻だけを持つ。
    [Fact]
    public async Task 読めない文書はページに入らず_続きの印も読めない文書を指さない()
    {
        await using var db = NewLedger();
        var a = await SeedAsync(db, "a", T0.AddMinutes(5));
        var hidden1 = await SeedAsync(db, "hidden-1", T0.AddMinutes(4), PrivateNote("alice"));
        var b = await SeedAsync(db, "b", T0.AddMinutes(3));
        var hidden2 = await SeedAsync(db, "hidden-2", T0.AddMinutes(2), PrivateNote("alice"));
        var c = await SeedAsync(db, "c", T0.AddMinutes(1));

        // 陽性対照: 所有者には個人資料も返る（同じ台帳で、読めない文書が実在することを先に確かめる）。
        var (ownerIds, _) = await ReadAllAsync(Reads(db), DocumentReadPrincipal.RelayedUser("alice"), pageSize: 10);
        ownerIds.Should().Contain([hidden1.Id, hidden2.Id]);

        var reads = Reads(db);
        var (first, next) = await reads.ListPageAsync(Machine, 2, null, Ct);
        first.Select(d => d.Id).Should().Equal(a.Id, b.Id);
        next.Should().NotBeNull();
        DocumentListCursor.TryDecode(next, out var cursor).Should().BeTrue();
        cursor.Id.Should().Be(b.Id, "印は返した最後の文書から作る");
        cursor.Id.Should().NotBe(hidden2.Id);

        var (rest, _) = await ReadAllAsync(reads, Machine, pageSize: 2);
        rest.Should().Equal(a.Id, b.Id, c.Id);
    }

    // 読めない文書が塊の大きさを超えて続いても、その先の読める文書へ届く（塊読みが途中で止まらない）。
    [Fact]
    public async Task 読めない文書が長く続いてもその先の読める文書へ届く()
    {
        await using var db = NewLedger();
        var last = await SeedAsync(db, "visible-oldest", T0);
        for (var i = 1; i <= 7; i++)
            await SeedAsync(db, $"hidden-{i}", T0.AddMinutes(i), PrivateNote("alice"));
        var first = await SeedAsync(db, "visible-newest", T0.AddMinutes(10));

        var (page, next) = await Reads(db).ListPageAsync(Machine, 2, null, Ct);

        page.Select(d => d.Id).Should().Equal(first.Id, last.Id);
        next.Should().BeNull();
    }

    // ── 続きの印の改ざん ───────────────────────────────────────────

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("djE6MTIzOmFiYw")]   // "v1:123:abc"（作成日時の口の版の札）
    [InlineData("dTE6MTIz")]         // "u1:123"（項目が足りない）
    [InlineData("dTE6LTE6MDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDE")] // 負の時刻
    public void 壊れた印は読めない(string token)
    {
        DocumentListCursor.TryDecode(token, out _).Should().BeFalse();
    }

    // 表せない時刻の印は壊れた印として扱う（通すと時刻の組み立てで例外になり UNKNOWN で落ちる）。
    [Fact]
    public void 表せない時刻の印は読めない()
    {
        var token = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            $"u1:{long.MaxValue}:{Guid.NewGuid():N}"));

        DocumentListCursor.TryDecode(token, out _).Should().BeFalse();
    }

    // 作成日時の口（`GET /documents/page`）の印を渡しても読めない（並びのキーも向きも違う。黙って別の位置から読まない）。
    [Fact]
    public void 作成日時の口の印はこの口では読めない()
    {
        var other = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            $"v1:{T0.UtcTicks}:{Guid.NewGuid():N}"));

        DocumentListCursor.TryDecode(other, out _).Should().BeFalse();
    }

    [Fact]
    public void 印は往復で値が変わらない()
    {
        var cursor = new DocumentListCursor(T0.UtcTicks, Guid.NewGuid());

        DocumentListCursor.TryDecode(cursor.Encode(), out var decoded).Should().BeTrue();
        decoded.Should().Be(cursor);
    }

    // ── ページの大きさの丸め ──────────────────────────────────────

    [Theory]
    [InlineData(0, DocumentReadUseCase.DefaultPageSize)]
    [InlineData(1, 1)]
    [InlineData(DocumentReadUseCase.MaxPageSize, DocumentReadUseCase.MaxPageSize)]
    [InlineData(DocumentReadUseCase.MaxPageSize + 1, DocumentReadUseCase.MaxPageSize)]
    [InlineData(int.MaxValue, DocumentReadUseCase.MaxPageSize)]
    public void ページの大きさは0なら既定_上限を超えたら上限に丸める(int requested, int expected)
    {
        DocumentReadGrpcService.PageSizeOf(requested).Should().Be(expected);
    }

    [Fact]
    public void 負のページの大きさは要求の誤りである()
    {
        var act = () => DocumentReadGrpcService.PageSizeOf(-1);

        act.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }
}
