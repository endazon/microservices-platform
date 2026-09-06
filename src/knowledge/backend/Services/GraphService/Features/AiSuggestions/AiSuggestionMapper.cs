using GraphService.Domain;
using Knowledge.Contracts.Dtos;
using Riok.Mapperly.Abstractions;

namespace GraphService.Features.AiSuggestions;

// FR-18, SC-21, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly。選定基準 4「実行時リフレクションより
// コンパイル時生成を優先する」）/ IADR-0371 決定 3 / IADR-0393 / IADR-0406: ドメイン → DTO の写像。
//
// 従前は `AiSuggestionEndpoints.ToDto` の手書き詰め替えであった。**追加引数を 3 つ伴う**が、
// 3 つとも**そのまま 1 つの対象メンバに載る完成値**である（IADR-0406 決定 1 の「材料」）——
// 端点が既に解決し終えた両端の表示名と、既に判定し終えた承認資格の bool である。
//
// 🔴 **引数名は対象メンバ名と一致させる。** Mapperly は追加引数を**名前一致でしか**結び付けず、
// `[MapProperty("sourceTitle", …)]` のような改名は **RMG006 でコンパイルエラー**になる（実測）。
// 綴りを違えると引数は捨てられ、**警告 1 本（RMG082）でビルドは通る** ——
// `AiSuggestionDto` の末尾 3 列は既定値つきなので、応答は `""` / `null` / `false` に化ける。
// `src/Directory.Build.props` が RMG012 / RMG082 を error へ上げているのはこの型を止めるためである。
//
// 🔴 **認可の「判定」はここに入れない。** 入っているのは判定**結果**の名（`canDecide`）だけであり、
// これは契約が既に持つ語である（`AiSuggestionDto.CanDecide`。ADR-0063 決定 3〜5 / IADR-0364 決定 4
// 「資格はサーバが判定し `CanDecide` で行ごとに運ぶ」）。`CanDecideAsync` / `ClaimsPrincipal` /
// `AccessScopeResponse` / `IsInRole` は登録表（`AiSuggestionEndpoints`）に残る（IADR-0406 決定 3）。
// **既定 `false` は deny 側である** —— 載せ忘れた経路は「できない」と描かれる。
//
// **置き場は 2 段目（`Features/AiSuggestions/`）である。** 承認・却下・一覧・生成の
// **4 操作が使う**ためであり、`ADR-0068` 決定 2 の適用結果である。**手書きだった頃と変わらない。**
//
// 生成コードは `obj/` 配下に出るため、カバレッジ集計からは既に落ちている（IADR-0195 決定 1）。
// **床は動かない。**
[Mapper]
internal static partial class AiSuggestionMapper
{
    // FR-18, SC-21: AI 提案 → 応答 DTO。実体は source generator が生成する。
    //
    // 🔴 **本文指紋を公開面へ出さない**（`EdgeTypeDictionaryDto.cs` の宣言。ADR-0033 決定 10）。
    // 指紋は却下解除の判定に使う内部状態であり、出すと「文書の内容が変わったか」を
    // 文書を読めない利用者にも判定させる副次経路になる。
    // **「たまたま DTO に同名の欄が無いから落ちた」と「落とすと決めた」を区別できる形にする** ——
    // `[MapperIgnoreSource]` を書いておけば、DTO 側に欄を足した誰かが黙って露出させることはできない。
    [MapperIgnoreSource(nameof(AiSuggestion.SourceFingerprint))]
    [MapperIgnoreSource(nameof(AiSuggestion.TargetFingerprint))]
    // 却下・再提示の時刻は画面が使わない（SC-21 が描くのは理由と回数である）。
    [MapperIgnoreSource(nameof(AiSuggestion.RejectedAt))]
    [MapperIgnoreSource(nameof(AiSuggestion.ReinstatedAt))]
    [MapperIgnoreSource(nameof(AiSuggestion.CreatedAt))]
    internal static partial AiSuggestionDto ToDto(
        AiSuggestion s, string sourceDocumentTitle, string? targetDocumentTitle,
        bool canDecide = false);
}
