using DocumentService.Domain;
using Knowledge.Contracts.Dtos;
using Riok.Mapperly.Abstractions;

namespace DocumentService.Features.Documents;

// FR-06, UC-03, SC-03, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly。選定基準 4「実行時
// リフレクションより コンパイル時生成を優先する」）/ IADR-0371 決定 3 / IADR-0393 / IADR-0406:
// ドメイン → DTO の写像 2 本。
//
// 従前は `DocumentEndpoints.ToDto` / `.ToVersionDto` の手書き詰め替えであった。
//
// 🔴 **タグ名の辞書は写像に入れない**（IADR-0406 決定 1）。移送前がやっていた
// `TagResolver.ToNames(d.Tags, names)` は**辞書引きという導出の指示**であり、材料ではない。
// 追加引数として渡すのは**解決済みの `List<string> tags`** であり、辞書引きは端（登録表）に残る
// —— そこが識別子 → 表示名の変換点を 1 つに閉じている場所である（IADR-0153 決定 2）。
//
// 🔴 **辞書をそのまま渡す形は「緑のビルドで誤った本番データ」を生む**（本 PR で実測）。
// 追加引数は**対象メンバ名との一致でしか**結び付かないので、`names` という名前の辞書は捨てられ、
// Mapperly は源の同名メンバから `Tags = MapToListOfString(d.Tags)` を合成する ——
// 応答の `Tags` に**タグ名ではなく GUID 文字列**が入る。既定ではこれが **RMG082 警告 1 本**であり、
// `src/Directory.Build.props` の error 化だけがこれを止めている。
//
// 🔴 **引数名は対象メンバ名と一致させる。** Mapperly は追加引数を名前一致でしか結び付けず、
// `[MapProperty("names", …)]` のような改名は RMG006 でコンパイルエラーになる（実測）。
//
// **置き場は 2 段目（`Features/Documents/`）である。** `ToDto` は 9 操作、`ToVersionDto` は
// 2 操作が使うためであり、`ADR-0068` 決定 2 の適用結果である。**手書きだった頃と変わらない。**
//
// 生成コードは `obj/` 配下に出るため、カバレッジ集計からは既に落ちている（IADR-0195 決定 1）。
// **床は動かない。**
[Mapper]
internal static partial class DocumentMapper
{
    // FR-06, UC-03, SC-03: 文書 → 応答 DTO。実体は source generator が生成する。
    // 契約（`DocumentDto.Tags`）は `List<string>` のままで、**下流も画面も変わらない**。
    //
    // **`DocumentDto` は `Document` の部分射影である**（原本 URI・種別・指紋・添付・取込元・版履歴・
    // 公開可否は応答に出さない）。これを 8 個の `[MapperIgnoreSource]` で並べると、
    // 🔴 な省略（下の `MarkdownUri` や `SyncDevice.TokenHash` の類）が些事に埋もれる ——
    // **「部分射影である」ことは 1 回だけ宣言し、`[MapperIgnoreSource]` は
    // 「これを出さないと決めた」という個別の合図に取っておく**（IADR-0406 決定 7）。
    // RMG012（対象側の取りこぼし）は宣言しても効いたままである（実測）。
    [MapperRequiredMapping(RequiredMappingStrategy.Target)]
    // 🔴 これは部分射影の一部ではなく**載せ替え**である。源の `Tags` は `List<Guid>`、対象は
    // `List<string>` で、**同名の列が 2 通りに埋まり得る**。実測では 4.3.1 は名前の一致する
    // 追加引数を優先するので本属性を外しても `Tags = tags` のままだが、**それは黙って正しいだけで、
    // 「どちらを採るか決めた」ことがコードに現れない**。属性を書いておけば源の側が候補から外れる。
    //
    // 🔴 **引数名を `names` のように変えた瞬間に壊れる**（実測）：追加引数は捨てられ、
    // `Tags = MapToListOfString(d.Tags)` —— **タグ名ではなく GUID 文字列**が生成される。
    // これを止めているのは RMG082 の error 化だけである（`NoWarn` を足すと 0 警告で通る）。
    [MapperIgnoreSource(nameof(Document.Tags))]
    // FR-19, ADR-0036 D-06, ADR-0098 決定 1, [[IADR-0447]] (#1447): 共有先（`DocumentDto.SharedWith`）は
    // **源（`Document`）に無い** —— 共有は属性辞書ではなく専用の台帳（`DocumentShare`）が持つ
    // （[[IADR-0253]] 決定 4）。したがって `tags` と同じ**追加引数**で受ける。
    // 🔴 **引数名は対象メンバ名（`SharedWith`）と一致させる**（上の `tags` と同じ理由。
    // 改名すると RMG006 でコンパイルエラー、または黙って捨てられる）。
    // 解決点は `DocumentEndpoints.ResolveSharedWithAsync` ただ 1 つである。
    internal static partial DocumentDto ToDto(Document d, List<string> tags, List<string>? sharedWith);

    // **過去版も現在の表示名で出る** —— 改名は表示上の変更である（[[IADR-0153]] 決定 4）。
    //
    // 🔴 **本文の参照は載せない**（#1011 / [[IADR-0290]]）。`DocumentVersion.MarkdownUri` は
    // スナップショット時点の**文書の**本文 URI であり、オブジェクトキーが文書 ID で固定のため
    // **常に現行版の本文を指す**。載せると 200 の応答に「その版の本文らしい URI」が入り、
    // 呼び出し側が過去版の本文だと読み違えても区別できない。**戻さないこと。**
    //
    // この省略は 3 層で可視である（IADR-0406 決定 5）——
    //   ① 下の `[MapperIgnoreSource]`。**これが無いと、Mapperly の沈黙（DTO に欄が無いので
    //      診断は 1 件も出ない）は「事故で落ちた」と区別がつかない。**
    //   ② `DocumentVersionDto` に `MarkdownUri` を戻すと **RMG012 でビルドが赤になる**
    //      （`src/Directory.Build.props` の error 化。実測済み）。属性を消す明示の操作が要る。
    //   ③ `DocumentMapperTests.Dto_HasNoMarkdownUriMember`（契約側の事実そのもの）。
    [MapperRequiredMapping(RequiredMappingStrategy.Target)]
    [MapperIgnoreSource(nameof(DocumentVersion.MarkdownUri))]
    [MapperIgnoreSource(nameof(DocumentVersion.Tags))]
    // 版の代理鍵は契約に出さない（画面が指すのは `DocumentId` ＋ `Version` の組である）。
    [MapperIgnoreSource(nameof(DocumentVersion.Id))]
    internal static partial DocumentVersionDto ToVersionDto(DocumentVersion v, List<string> tags);
}
