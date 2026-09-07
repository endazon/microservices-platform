using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.PrivateNotes;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.PrivateNotes;

// FR-19, UC-11, SC-19, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly）/ IADR-0371 決定 3 /
// IADR-0393 / IADR-0406: 手書きの詰め替えを生成マッパへ置き換えた際の**振る舞い同値**を固定する。
//
// 🔴 **生成物を信じるのではなく、写った値を見る。** 14 列を 1 つずつ見る。
// このマッパは `[MapProperty]` を 3 本持つ（`DocumentId → Id` / `LatestBytes → Bytes` /
// `IsDeleted → Deleted`）—— **名前が違う列こそ黙って落ちる側**である。
//
// 端に残した縮退（`doc?.Title ?? ""` / `doc?.Version ?? 0`）は**本ファイル末尾の対**が見る
// （#1312。以前は `PrivateNoteEndpointsMappingTests` を指していたが、その名前の試験は存在しなかった）。
[Trait("TestKind", "Unit")]
public class PrivateNoteMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static PrivateNote NewNote() =>
        PrivateNote.Create(Guid.NewGuid(), "alice", "研究/メモ.md", 2048, "hash-1", Now);

    // 陽性: 全 14 列が値を保ったまま写る（追加引数の `Title` / `Version` を含む）。
    [Fact]
    public void ToDto_CopiesEveryProperty()
    {
        var n = NewNote();
        n.SetExposure(includeInSearch: true, includeInGraph: false, includeInAi: true, Now);

        var dto = PrivateNoteMapper.ToDto(n, "研究メモ", 7);

        dto.Id.Should().Be(n.DocumentId);
        dto.Title.Should().Be("研究メモ");
        dto.VaultPath.Should().Be("研究/メモ.md");
        dto.Version.Should().Be(7);
        dto.Bytes.Should().Be(2048);
        dto.ContentHash.Should().Be("hash-1");
        dto.IncludeInSearch.Should().BeTrue();
        dto.IncludeInGraph.Should().BeFalse();
        dto.IncludeInAi.Should().BeTrue();
        dto.Deleted.Should().BeFalse();
        dto.DeletedAt.Should().BeNull();
        dto.PurgeAt.Should().BeNull();
        dto.CreatedAt.Should().Be(Now);
        dto.UpdatedAt.Should().Be(Now);
    }

    // 陰性: 任意項目の null は null のまま写る。
    // 🔴 **`PurgeAt` が既定日時へ倒れると、SC-19 の「残り日数」が意味を持ってしまう** ——
    // 削除されていない資料に期限は無い。
    [Fact]
    public void ToDto_KeepsNullOptionalFields()
    {
        var n = PrivateNote.Create(Guid.NewGuid(), "bob", "雑記.md", 0, contentHash: null, Now);

        var dto = PrivateNoteMapper.ToDto(n, string.Empty, 0);

        dto.ContentHash.Should().BeNull();
        dto.DeletedAt.Should().BeNull();
        dto.PurgeAt.Should().BeNull();
        dto.Bytes.Should().Be(0);
    }

    // 陰性 2: 論理削除・復元の状態が写り直る（`IsDeleted → Deleted` の名前替えが効いている）。
    [Fact]
    public void ToDto_ReflectsSoftDeleteAndRestore()
    {
        var n = NewNote();
        n.SoftDelete(Now);

        var deleted = PrivateNoteMapper.ToDto(n, "研究メモ", 1);
        deleted.Deleted.Should().BeTrue();
        deleted.DeletedAt.Should().Be(Now);
        deleted.PurgeAt.Should().Be(Now.AddDays(90));

        n.Restore(Now.AddDays(1));

        var restored = PrivateNoteMapper.ToDto(n, "研究メモ", 1);
        restored.Deleted.Should().BeFalse();
        restored.DeletedAt.Should().BeNull();
        restored.PurgeAt.Should().BeNull();
    }

    // 🔴 **追加引数はそのまま写る。** 縮退はここではなく端が行う ——
    // 空文字と 0 を渡せば空文字と 0 が出る（写像は何も補わない）。
    [Fact]
    public void ToDto_CopiesTitleAndVersionVerbatim()
    {
        var n = NewNote();

        var dto = PrivateNoteMapper.ToDto(n, string.Empty, 0);

        dto.Title.Should().BeEmpty();
        dto.Version.Should().Be(0);
    }

    // 🔴 **所有者は応答に載らない**（`PrivateNoteDto.cs`「主体を運ぶ口を作らない」/ ADR-0036）。
    // 生成マッパにも `[MapperIgnoreSource]` で明示してある。**DTO 側に欄を足せば
    // RMG012 でビルドが止まる**が、この試験は「今の DTO に所有者の欄が無い」ことを固定する。
    [Fact]
    public void Dto_HasNoOwnerMember()
    {
        typeof(PrivateNote).GetProperty("OwnerId").Should().NotBeNull(
            "台帳は所有者を持つ（落とすと決めた対象が実在することの陽性対照）");

        typeof(PrivateNoteDto).GetProperty("OwnerId").Should().BeNull(
            "主体は JWT からしか採らない（ADR-0036）");
        typeof(PrivateNoteDto).GetProperty("Owner").Should().BeNull(
            "主体は JWT からしか採らない（ADR-0036）");
    }

    // ---- #1312: 端に残した縮退（`doc?.Title ?? ""` / `doc?.Version ?? 0`）を固定する ----------
    //
    // 🔴 この注記はもともと「`PrivateNoteEndpointsMappingTests` が見る」と書いていたが、
    // **その名前の試験は存在しなかった**（#1312）。指し先を直すのではなく**試験を足す**。
    //
    // 縮退が要るのは、資料に対応する文書の複製がまだ届いていない場合である
    // （`PrivateNoteEndpoints.cs:112`）。**生成マッパへ持ち込むと `?? throw` に化ける**ため、
    // ここは端の判断として残してある（`PrivateNoteMapper.cs:14`）。**その端を測る。**

    // 陰性側: 文書がまだ届いていない資料は、題が空・版が 0 で返る（例外にしない）。
    [Fact]
    public void ToDto_WhenTheDocumentIsNotYetReplicated_FallsBackToEmptyTitleAndVersionZero()
    {
        var n = NewNote();

        var dto = PrivateNoteEndpoints.ToDto(n, doc: null);

        dto.Title.Should().BeEmpty("文書がまだ届いていないだけであり、例外にしない");
        dto.Version.Should().Be(0, "版が分からないことを 0 で表す");
        // 陽性対照: 縮退するのは題と版だけで、資料側の列はそのまま写る。
        dto.Id.Should().Be(n.DocumentId);
        dto.VaultPath.Should().Be("研究/メモ.md");
        dto.Bytes.Should().Be(2048);
    }

    // 陽性側（対）: 文書が届いていれば、その題と版がそのまま載る。
    // 🔴 これが無いと「常に空と 0 を返す」実装が上の試験を通してしまう。
    [Fact]
    public void ToDto_WhenTheDocumentIsPresent_CarriesItsTitleAndVersion()
    {
        var n = NewNote();
        var doc = Document.CreateNormalized(n.DocumentId, "研究メモ", "s3://bucket/note.md");

        var dto = PrivateNoteEndpoints.ToDto(n, doc);

        dto.Title.Should().Be("研究メモ");
        dto.Version.Should().Be(doc.Version);
    }
}
