using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using Riok.Mapperly.Abstractions;

namespace AuthorizationService.Features.Users;

// SC-17, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly。選定基準 4「実行時リフレクションより
// コンパイル時生成を優先する」）/ IADR-0371 決定 3 / IADR-0393: 身元 → 応答 DTO の写像。
//
// 従前は `UserAdminEndpoints.ToDto` の手書き詰め替え 1 本であった。`IdentityUser` と
// `PlatformUserDto` は 6 プロパティすべて同名の 1:1 であり、Mapperly の既定規約でそのまま写る。
//
// 🔴 **コレクションは複製される。** 移送前も `[.. user.Roles]` と `new Dictionary<…>(…)` で
// 複製していた（`IReadOnlyList` / `IReadOnlyDictionary` を `List` / `Dictionary` へ受け直すため
// 生成側も複製する）。**応答 DTO が後段の書き換えで動かない**という性質は変わらない。
//
// **置き場は 2 段目（`Features/Users/`）である。** 一覧・属性差し替え・ロール差し替え・
// 無効化・再有効化の **5 操作が使う**ためであり、`ADR-0068` 決定 2 の適用結果である。
// **手書きだった頃と変わらない。**
//
// 生成コードは `obj/` 配下に出るため、カバレッジ集計からは既に落ちている（IADR-0195 決定 1）。
// **床は動かない。**
//
// ★ FR-19, SC-17, SC-19, ADR-0082 決定 5, [[IADR-0428]] (#1392):
//   🔴 **予約キー（保持起点）は応答へ出さない。** SC-17 の権限編集ダイアログは行の属性を
//   そのまま下書きへ写して（`useUserPermissionEditor` の `{...editing.attributes}`）差し替え要求へ
//   送り返す。予約キーは ABAC 属性辞書に無いため、`UserAssignmentValidation.ValidateAttributes` が
//   「辞書に定義されていない」と **400** を返す —— **無効化済み利用者の属性編集が壊れる。**
//   落とす場所を写像の 1 か所に閉じるのは、5 つの端点すべてがここを通るからである
//   （端点ごとに落とすと、次に足した端点だけが漏らす）。
[Mapper]
internal static partial class PlatformUserMapper
{
    // SC-17: 認可基盤の利用者 → 応答 DTO。**予約キーを落としてから**生成した写像へ渡す。
    internal static PlatformUserDto ToDto(IdentityUser user)
        => ToDtoCore(user with { Attributes = RetentionAnchorAttributes.WithoutReserved(user.Attributes) });

    // 実体は source generator が生成する（6 プロパティすべて同名の 1:1）。
    private static partial PlatformUserDto ToDtoCore(IdentityUser user);
}
