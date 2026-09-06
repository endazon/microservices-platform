using AwesomeAssertions;
using DocumentService.Domain;
using DocumentService.Features.SyncDevices.List;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Tests.Features.SyncDevices;

// FR-20, UC-11, SC-20, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly）/ IADR-0371 決定 3 /
// IADR-0393 / IADR-0405: 手書きの詰め替えを生成マッパへ置き換えた際の**振る舞い同値**を固定する。
//
// 🔴 **時計は写像に入っていない**（IADR-0405 決定 4）。`Active` は追加引数として渡ってくるので、
// この試験は `true` / `false` を直接与えて写ることだけを見る。`IsActive(now)` 自体の振る舞いは
// ドメインの試験（`SyncDeviceTokenTests`）が持つ ——**二重に持たない**。
[Trait("TestKind", "Unit")]
public class SyncDeviceMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static SyncDevice NewDevice() => SyncDevice.Create("alice", "ノート PC", "hash-1", Now);

    // 陽性: 全 7 列が値を保ったまま写る（追加引数の `Active` を含む）。
    [Fact]
    public void ToDto_CopiesEveryProperty()
    {
        var d = NewDevice();
        d.TouchSync(Now.AddHours(1));

        var dto = SyncDeviceMapper.ToDto(d, active: true);

        dto.Id.Should().Be(d.Id);
        dto.DeviceName.Should().Be("ノート PC");
        dto.IssuedAt.Should().Be(Now);
        dto.ExpiresAt.Should().Be(Now.AddDays(30));
        dto.Revoked.Should().BeFalse();
        dto.LastSyncAt.Should().Be(Now.AddHours(1));
        dto.Active.Should().BeTrue();
    }

    // 陰性: 一度も同期していない端末の `LastSyncAt` は null のまま写る
    //（既定日時へ倒れると、画面は「同期済み」と描ける）。
    [Fact]
    public void ToDto_KeepsNullLastSyncAt()
    {
        SyncDeviceMapper.ToDto(NewDevice(), active: true).LastSyncAt.Should().BeNull();
    }

    // 🔴 **`Revoked` は `RevokedAt` の有無に従う**（`Use = nameof(IsRevoked)` の 1 メンバ変換）。
    // 指名を外すと `DateTimeOffset?` → `bool` の変換が無くなりビルドが止まる。
    [Fact]
    public void Revoked_IsTrueIffRevokedAtIsSet()
    {
        var d = NewDevice();
        SyncDeviceMapper.ToDto(d, active: true).Revoked.Should().BeFalse();

        d.Revoke(Now.AddDays(1));

        SyncDeviceMapper.ToDto(d, active: false).Revoked.Should().BeTrue();
    }

    // 🔴 **追加引数はそのまま写る。** `Active` は写像が計算した値ではないので、
    // 失効済み・期限切れの端末に `true` を渡せば `true` が出る ——
    // **判定は呼び出し側（`ListSyncDevicesEndpoint`）の責任である**ことを型で示す。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToDto_CopiesActiveVerbatim(bool active)
    {
        SyncDeviceMapper.ToDto(NewDevice(), active).Active.Should().Be(active);
    }

    // 🔴 **トークンのハッシュも所有者も応答に載らない**（`PrivateNoteDto.cs` の宣言 / ADR-0036）。
    // 生成マッパにも `[MapperIgnoreSource]` で明示してある。
    [Fact]
    public void Dto_HasNoTokenOrOwnerMember()
    {
        typeof(SyncDevice).GetProperty("TokenHash").Should().NotBeNull(
            "台帳はハッシュを持つ（落とすと決めた対象が実在することの陽性対照）");

        typeof(SyncDeviceDto).GetProperty("TokenHash").Should().BeNull(
            "トークンは平文もハッシュも一覧に載せない（SC-20）");
        typeof(SyncDeviceDto).GetProperty("Token").Should().BeNull(
            "平文が現れるのは発行・再発行の応答だけである（SC-20）");
        typeof(SyncDeviceDto).GetProperty("OwnerId").Should().BeNull(
            "主体は JWT からしか採らない（ADR-0036）");
    }
}
