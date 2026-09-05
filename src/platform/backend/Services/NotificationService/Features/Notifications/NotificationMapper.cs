using NotificationService.Domain;
using Riok.Mapperly.Abstractions;

namespace NotificationService.Features.Notifications;

// FR-22, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly。選定基準 4「実行時リフレクションより
// コンパイル時生成を優先する」）/ IADR-0371 決定 3 / IADR-0393: ドメイン → DTO の写像。
//
// 従前は `NotificationStore.ToDto` の手書き詰め替え 1 本であった。`Notification` の 7 プロパティが
// `NotificationDto` と同名の 1:1 であり、Mapperly の既定規約でそのまま写る。
//
// 🔴 **`Subject`（宛先）は明示的に落とす。** DTO は本人宛の一覧としてしか返らないため宛先を
// 持たないが、**「たまたま名前が一致しないから落ちた」と「落とすと決めた」を区別できる形にする**
// —— `[MapperIgnoreSource]` を書いておけば、DTO 側に `Subject` を足した誰かが
// **黙って宛先を露出させる**ことはできない（属性を消す明示の操作が要る）。
//
// **置き場は 2 段目（`Features/Notifications/`）である。** 手書きだった頃と同じ場所であり、
// 読み出し口を持つ `NotificationStore` と同居する（ADR-0068 決定 2）。
//
// 生成コードは `obj/` 配下に出るため、カバレッジ集計からは既に落ちている（IADR-0195 決定 1）。
// **床は動かない。**
[Mapper]
internal static partial class NotificationMapper
{
    // FR-22: 保存済み通知 → 応答 DTO。実体は source generator が生成する。
    [MapperIgnoreSource(nameof(Notification.Subject))]
    internal static partial NotificationDto ToDto(Notification notification);
}
