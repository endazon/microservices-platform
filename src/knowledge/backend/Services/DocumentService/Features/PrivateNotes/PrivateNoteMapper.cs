using DocumentService.Domain;
using Knowledge.Contracts.Dtos;
using Riok.Mapperly.Abstractions;

namespace DocumentService.Features.PrivateNotes;

// FR-19, UC-11, SC-19, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly。選定基準 4「実行時
// リフレクションより コンパイル時生成を優先する」）/ IADR-0371 決定 3 / IADR-0393 / IADR-0406:
// ドメイン → DTO の写像。
//
// 従前は `PrivateNoteEndpoints.ToDto(PrivateNote n, Document? doc)` の手書き詰め替えであった。
//
// 🔴 **文書（`Document?`）そのものは写像に入れない**（IADR-0406 決定 1）。
// 移送前がやっていた `doc?.Title ?? string.Empty` / `doc?.Version ?? 0` は**導出の指示**であり、
// 材料ではない。**Mapperly では書けもしない** —— 追加引数のメンバ取り出し（`"doc.Title"`）は
// RMG006 でコンパイルエラーになり（追加引数は入れ子の写像へ渡らない）、
// 🔴 かつ **null 許容の引数を非 null のコンストラクタ引数へ渡すと
// `?? throw new ArgumentNullException` が生成される**（`?? string.Empty` にはならない・実測）。
// つまり縮退を写像へ持ち込むと、**題の無い資料が 500 になる**。縮退は端（登録表）に残す。
//
// 🔴 **引数名は対象メンバ名と一致させる。** Mapperly は追加引数を名前一致でしか結び付けない。
//
// **置き場は 2 段目（`Features/PrivateNotes/`）である。** 作成・一覧・復元・露出更新の
// **4 操作が使う**ためであり、`ADR-0068` 決定 2 の適用結果である。**手書きだった頃と変わらない。**
//
// 生成コードは `obj/` 配下に出るため、カバレッジ集計からは既に落ちている（IADR-0195 決定 1）。
// **床は動かない。**
[Mapper]
internal static partial class PrivateNoteMapper
{
    // FR-19, SC-19: 個人資料の台帳 1 行 → 応答 DTO。実体は source generator が生成する。
    //
    // 🔴 **所有者を公開面へ出さない**（`PrivateNoteDto.cs` の宣言「主体を運ぶ口を作らない」）。
    // 後段は主体を JWT からしか採らず、DTO に `ownerId` を置くと「誰の資料か」を要求側が
    // 指定できる形に見え、いずれ実装がそれを読む。**個人資料は本人のみ**（ADR-0036）を型でも守る。
    // **「たまたま名前が一致しないから落ちた」と「落とすと決めた」を区別できる形にする。**
    [MapperIgnoreSource(nameof(PrivateNote.OwnerId))]
    // 完全削除予告の通知済み時刻は運用の内部状態であり、画面は `PurgeAt` だけを使う。
    [MapperIgnoreSource(nameof(PrivateNote.PurgeImminentNotifiedAt))]
    [MapProperty(nameof(PrivateNote.DocumentId), nameof(PrivateNoteDto.Id))]
    [MapProperty(nameof(PrivateNote.LatestBytes), nameof(PrivateNoteDto.Bytes))]
    [MapProperty(nameof(PrivateNote.IsDeleted), nameof(PrivateNoteDto.Deleted))]
    internal static partial PrivateNoteDto ToDto(PrivateNote n, string title, int version);
}
