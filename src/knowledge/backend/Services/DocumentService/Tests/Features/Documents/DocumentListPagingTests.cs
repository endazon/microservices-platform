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
    private static Task<(List<Guid> Ids, List<int> PageSizes)> ReadAllAsync(
        DocumentReadUseCase reads, DocumentReadPrincipal principal, int pageSize)
        => ReadAllAsync(reads, principal, pageSize, DocumentListSortKey.UpdatedDesc);

    private static async Task<(List<Guid> Ids, List<int> PageSizes)> ReadAllAsync(
        DocumentReadUseCase reads, DocumentReadPrincipal principal, int pageSize, DocumentListSortKey sort)
    {
        var ids = new List<Guid>();
        var sizes = new List<int>();
        DocumentListCursor? after = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var (page, next) = await reads.ListPageAsync(principal, sort, pageSize, after, Ct);
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

    // ── 並べ替え（[[IADR-0545]] / #1923 段 2） ─────────────────────────────

    // 表題・作成日時・更新日時に同値を混ぜた台帳。キーの同値は ID で並ぶ（キーと同じ向き）。
    private static async Task<List<Document>> SeedSortableAsync(DocumentDbContext db)
    {
        var docs = new List<Document>();
        (string Title, int Created, int Updated)[] rows =
        [
            ("b", 3, 1), ("a", 1, 4), ("b", 2, 2), ("c:コロン", 2, 2), ("a", 4, 3), ("決算", 1, 5), ("b", 5, 1),
        ];
        for (var i = 0; i < rows.Length; i++)
        {
            var (title, created, updated) = rows[i];
            var doc = await SeedAsync(db, title, T0.AddMinutes(updated), id: Guid.Parse($"00000000-0000-0000-0000-00000000000{i + 1}"));
            var tracked = await db.Documents.SingleAsync(d => d.Id == doc.Id, Ct);
            db.Entry(tracked).Property(d => d.CreatedAt).CurrentValue = T0.AddMinutes(created);
            await db.SaveChangesAsync(Ct);
            db.ChangeTracker.Clear();
            docs.Add(await db.Documents.AsNoTracking().SingleAsync(d => d.Id == doc.Id, Ct));
        }
        return docs;
    }

    private static List<Guid> Expected(IEnumerable<Document> docs, DocumentListSortKey sort) => (sort switch
    {
        DocumentListSortKey.UpdatedAsc => docs.OrderBy(d => d.UpdatedAt).ThenBy(d => d.Id),
        DocumentListSortKey.CreatedDesc => docs.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id),
        DocumentListSortKey.CreatedAsc => docs.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id),
        DocumentListSortKey.TitleAsc => docs.OrderBy(d => d.Title).ThenBy(d => d.Id),
        DocumentListSortKey.TitleDesc => docs.OrderByDescending(d => d.Title).ThenByDescending(d => d.Id),
        _ => docs.OrderByDescending(d => d.UpdatedAt).ThenByDescending(d => d.Id),
    }).Select(d => d.Id).ToList();

    // 6 通りの並べ替えで、印を 2 件ずつ辿った結果がキー（同値は ID）の全順序と一致し、重複も抜けも無い。
    // 境界は同値の群（表題 b が 3 件・作成日時 2 が 2 件・更新日時 1 が 2 件）の間に落ちる。
    [Theory]
    [InlineData(nameof(DocumentListSortKey.UpdatedDesc))]
    [InlineData(nameof(DocumentListSortKey.UpdatedAsc))]
    [InlineData(nameof(DocumentListSortKey.CreatedDesc))]
    [InlineData(nameof(DocumentListSortKey.CreatedAsc))]
    [InlineData(nameof(DocumentListSortKey.TitleAsc))]
    [InlineData(nameof(DocumentListSortKey.TitleDesc))]
    public async Task 並べ替えごとに印を辿るとキーとIDの全順序で1回ずつ返る(string sortName)
    {
        var sort = Enum.Parse<DocumentListSortKey>(sortName);
        await using var db = NewLedger();
        var docs = await SeedSortableAsync(db);

        var (ids, sizes) = await ReadAllAsync(Reads(db), Machine, pageSize: 2, sort);

        ids.Should().Equal(Expected(docs, sort));
        sizes.Should().Equal(2, 2, 2, 1);
    }

    // 並べ替えを変えても、読めない文書はページにも印にも出ない（印は返した最後の文書の表題と ID だけを持つ）。
    [Fact]
    public async Task 表題の並べ替えでも読めない文書はページにも印にも出ない()
    {
        await using var db = NewLedger();
        var a = await SeedAsync(db, "a", T0);
        var hidden = await SeedAsync(db, "b-秘", T0, PrivateNote("alice"));
        var c = await SeedAsync(db, "c", T0);

        var (page, next) = await Reads(db).ListPageAsync(Machine, DocumentListSortKey.TitleAsc, 1, null, Ct);

        page.Select(d => d.Id).Should().Equal(a.Id);
        DocumentListCursor.TryDecode(next, out var cursor).Should().BeTrue();
        (cursor.Sort, cursor.Title, cursor.Id).Should().Be((DocumentListSortKey.TitleAsc, "a", a.Id));
        var (all, _) = await ReadAllAsync(Reads(db), Machine, pageSize: 1, DocumentListSortKey.TitleAsc);
        all.Should().Equal(a.Id, c.Id).And.NotContain(hidden.Id);
    }

    // 🔴 印の並べ替えと要求の並べ替えが違えば読まない（黙って別の位置から読まない）。
    [Fact]
    public async Task 印の並べ替えと要求の並べ替えが違えば例外()
    {
        await using var db = NewLedger();
        var cursor = new DocumentListCursor(DocumentListSortKey.TitleAsc, 0, "a", Guid.NewGuid());

        var act = async () => await Reads(db).ListPageAsync(Machine, DocumentListSortKey.UpdatedDesc, 2, cursor, Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // 並べ替えごとの印は往復で値が変わらない。表題は `:` や日本語を含んでも崩れない。既定の並びは従前の `u1` の札で書く。
    [Theory]
    [InlineData(nameof(DocumentListSortKey.UpdatedAsc), null)]
    [InlineData(nameof(DocumentListSortKey.CreatedDesc), null)]
    [InlineData(nameof(DocumentListSortKey.CreatedAsc), null)]
    [InlineData(nameof(DocumentListSortKey.TitleAsc), "決算:短信 a:b")]
    [InlineData(nameof(DocumentListSortKey.TitleDesc), "")]
    public void 並べ替えごとの印は往復で値が変わらない(string sortName, string? title)
    {
        var sort = Enum.Parse<DocumentListSortKey>(sortName);
        var isTitle = sort is DocumentListSortKey.TitleAsc or DocumentListSortKey.TitleDesc;
        var cursor = new DocumentListCursor(sort, isTitle ? 0 : T0.UtcTicks, isTitle ? title : null, Guid.NewGuid());

        DocumentListCursor.TryDecode(cursor.Encode(), out var decoded).Should().BeTrue();
        decoded.Should().Be(cursor);
        Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor.Encode())).Should().StartWith("s1:");
        Encoding.UTF8.GetString(Base64Url.DecodeFromChars(new DocumentListCursor(T0.UtcTicks, Guid.NewGuid()).Encode()))
            .Should().StartWith("u1:", "既定の並びの印は従前の形（配備の途中の新旧が読める）");
    }

    [Theory]
    [InlineData("s1:zz:0:00000000000000000000000000000001")]   // キーが不明
    [InlineData("s1:ud:0:00000000000000000000000000000001")]   // 既定の並びは u1 で書く（s1 には無い）
    [InlineData("s1:ta:!!:00000000000000000000000000000001")]  // 表題が base64url でない
    [InlineData("s1:ta:_w:00000000000000000000000000000001")]  // 表題が UTF-8 として壊れている（0xFF）
    [InlineData("s1:cd:abc:00000000000000000000000000000001")] // 時刻が数でない
    [InlineData("s1:cd:0:not-a-guid")]                         // ID が壊れた
    [InlineData("s1:cd:0")]                                    // 項目が足りない
    public void 壊れた並べ替えの印は読めない(string raw)
    {
        DocumentListCursor.TryDecode(Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw)), out _).Should().BeFalse();
    }

    // 線上の enum の写像。未定義の値は既定へ畳まず INVALID_ARGUMENT。
    [Fact]
    public void 未定義の並べ替えは要求の誤りである()
    {
        DocumentReadGrpcService.SortOf(Knowledge.Contracts.Grpc.Document.V1.DocumentListSort.Unspecified)
            .Should().Be(DocumentListSortKey.UpdatedDesc);
        var act = () => DocumentReadGrpcService.SortOf((Knowledge.Contracts.Grpc.Document.V1.DocumentListSort)99);

        act.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
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
