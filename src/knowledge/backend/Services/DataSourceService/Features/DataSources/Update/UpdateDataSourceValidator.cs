using FluentValidation;

namespace DataSourceService.Features.DataSources.Update;

// FR-01, UC-04, SC-06, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 /
// IADR-0395: 全置換（PUT）の入力規則。従前は Endpoint.cs 内の手書きガード節 1 本であった。
//
// 🔴 **振る舞いを変えない移送である。** 同じ 400・同じ本文（`{ "error": "..." }`）を返す。
//
// **`config` と `defaultAttributes` を 1 本の規則で見る。** 元は 1 本の `||` であり、
// 2 本へ割ると片方だけ省いたときと両方省いたときで違反の件数が変わる（`Errors[0]` は
// 同じでも、件数を見る試験が将来書かれたときに移送前と食い違う）。
//
// **移送したのはこの 1 箇所だけである。** 同じ端点の `ConnectionUriPolicy.Validate` は
// **既存値**（`ds.ConnectionUri`）を見るので、`db.DataSources.FindAsync` の後ろから動かせない
// （先頭へ上げると 404 が 400 に化ける）。`OwnerMappingValidation.ValidateAsync` は
// 外部の利用者名簿を引き、応答が RFC7807（全違反を返す）で形が違う。**どちらも端点に残す**
// （IADR-0395 決定 2）。
internal sealed class UpdateDataSourceValidator : AbstractValidator<UpdateDataSourceRequest>
{
    // FR-01: 元のガード節が返していた本文の文字列。**これが応答の契約である。**
    //
    // AI レビュー 🟡（#627）: **省略を受理しない。** PUT は全置換なので「省略 ＝ 空で置換」は
    // 意味論としては筋が通るが、契約が省略を許していると**うっかりで秘密が消える**
    // （`config` を送り忘れた PUT が apiToken を丸ごと落とす）。消したいなら `{}` と明示させる。
    internal const string FullReplacementRequiredMessage =
        "PUT は全置換です。config と defaultAttributes を明示してください"
        + "（消す場合は {} を送る）。一部だけ変更するなら PATCH を使ってください。";

    public UpdateDataSourceValidator()
    {
        // FR-01, UC-04: 全置換なので両方の明示が要る。
        // **null を現状維持にする道は採らない** —— それをやると PUT と PATCH の区別が消える。
        RuleFor(r => r)
            .Must(r => r.Config is not null && r.DefaultAttributes is not null)
            .WithMessage(FullReplacementRequiredMessage);
    }
}
