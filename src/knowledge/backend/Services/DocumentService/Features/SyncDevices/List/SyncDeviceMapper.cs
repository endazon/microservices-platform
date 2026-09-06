using DocumentService.Domain;
using Knowledge.Contracts.Dtos;
using Riok.Mapperly.Abstractions;

namespace DocumentService.Features.SyncDevices.List;

// FR-20, UC-11, SC-20, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly。選定基準 4「実行時
// リフレクションより コンパイル時生成を優先する」）/ IADR-0371 決定 3 / IADR-0393 / IADR-0406:
// ドメイン → DTO の写像。
//
// 従前は `SyncDeviceEndpoints.ToDto(SyncDevice d, DateTimeOffset now)` の手書き詰め替えであった。
//
// 🔴 **時計は写像に入れない**（IADR-0406 決定 4）。`Active` は `d.IsActive(now)` という**導出**であり、
// 「材料」ではない。追加引数は導出**済み**の `bool active` である。呼び出し側（`Endpoint.cs`）が
// `now` を作って `d.IsActive(now)` を済ませる —— そこは既に時計を持っている端である。
// 写像へ `DateTimeOffset` や `TimeProvider` を渡す形は、生成マッパをテストから固定しづらくする
// うえに、Mapperly では**そもそも書けない**（追加引数は入れ子の写像へ渡らないため、
// `MapPropertyFromSource` の変換器から `now` を参照できない）。
//
// 🔴 **引数名は対象メンバ名と一致させる。** Mapperly は追加引数を名前一致でしか結び付けず、
// `[MapProperty]` による改名は RMG006 でコンパイルエラーになる（実測）。
//
// 🔴 **`Revoked` の変換は `Use =` で明示的に指名する**（波 1 `McpClientMapper` の作法）。
// 指名せずに `DateTimeOffset? → bool` の変換メソッドを置くと、Mapperly は**型の組み合わせだけで**
// それを選ぶ —— 将来この写像へ別の `DateTimeOffset?` の列が入ったとき、黙って真偽値に化ける。
//
// **置き場は 3 段目（`Features/SyncDevices/List/`）である。** ⚠ **手書きだった頃とは場所が違う。**
// `ToDto` は登録表（`SyncDeviceEndpoints.cs`）に居たが、その登録表は 5 操作すべてが使う一方、
// **この写像を使う操作は一覧の 1 つだけ**である。`ADR-0068` 決定 2 は「そのファイルが
// 1 つの操作にしか使われないか」**だけ**で決めると定めているので、新しいファイルは 3 段目に置く。
//
// 生成コードは `obj/` 配下に出るため、カバレッジ集計からは既に落ちている（IADR-0195 決定 1）。
// **床は動かない。**
[Mapper]
internal static partial class SyncDeviceMapper
{
    // FR-20, SC-20: 同期端末 → 一覧の行。実体は source generator が生成する。
    //
    // 🔴 **トークンのハッシュを公開面へ出さない**（`PrivateNoteDto.cs` の宣言「トークンは平文も
    // ハッシュも載らない」）。**所有者もである** —— 主体は JWT からしか採らず、DTO へ置くと
    // 「誰の端末か」を要求側が指定できる形に見える（ADR-0036）。
    // **「たまたま DTO に同名の欄が無いから落ちた」と「落とすと決めた」を区別できる形にする** ——
    // `[MapperIgnoreSource]` を書いておけば、DTO 側に欄を足した誰かが黙って露出させることはできない。
    [MapperIgnoreSource(nameof(SyncDevice.OwnerId))]
    [MapperIgnoreSource(nameof(SyncDevice.TokenHash))]
    // 期限切れ通知の送信済み時刻は運用の内部状態であり、画面は使わない。
    [MapperIgnoreSource(nameof(SyncDevice.ExpiryNotifiedAt))]
    [MapProperty(nameof(SyncDevice.RevokedAt), nameof(SyncDeviceDto.Revoked), Use = nameof(IsRevoked))]
    internal static partial SyncDeviceDto ToDto(SyncDevice d, bool active);

    // SC-20: 失効したか。**失効時刻の有無そのもの**であり、時計を要らない
    //（`Active` と違って「期限内か」を見ない）。移送前の `d.RevokedAt is not null` と同じ式である。
    private static bool IsRevoked(DateTimeOffset? revokedAt) => revokedAt is not null;
}
