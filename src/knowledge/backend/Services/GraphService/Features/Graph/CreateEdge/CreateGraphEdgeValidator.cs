using FluentValidation;
using Knowledge.Contracts.Dtos;

namespace GraphService.Features.Graph.CreateEdge;

// FR-17, 計画 ADR-0030 §決定（検証 = FluentValidation）/ IADR-0371 決定 2 / IADR-0395:
// 辺の作成の入力規則。従前は Endpoint.cs 内の手書きガード節 2 本であった。
//
// 🔴 **振る舞いを変えない移送である。** 同じ 400・同じ本文（`{ "error": "..." }`）を返すため、
// 次の 2 点を守る:
//   1. **規則の宣言順を元のガード節の順に揃える**（両端の必須 → 自己ループ）。
//      FluentValidation は既定で全規則を走らせるが、呼び出し側が `Errors[0]` を採ることで
//      元の「最初の違反で返す」と同じ文字列になる。順序を入れ替えると本文が変わる ——
//      **両端とも空 Guid のとき、2 本目（自己ループ）も同時に違反する**（空 == 空）ので、
//      並びが逆だと `self_edge_not_allowed` が返るようになる。
//   2. **メッセージは元のリテラルをそのまま持ち上げる。** 定数にしたのは、テストが同じ文字列を
//      二度書いて片方だけ直す事故（文言だけ変わって誰も気づかない）を塞ぐためである。
//
// **`EdgeTypeId` の規則は無い。** 移送前も検証していない —— 型の実在は DB を引いた結果であり
// （`unknown_edge_type`）、認可の後ろにある。入力検証ではないので端点に残す。
internal sealed class CreateGraphEdgeValidator : AbstractValidator<CreateGraphEdgeRequest>
{
    // FR-17: 元のガード節が返していた本文の文字列。**この 2 本が応答の契約である。**
    internal const string DocumentIdRequiredMessage = "document_id_required";
    internal const string SelfEdgeNotAllowedMessage = "self_edge_not_allowed";

    public CreateGraphEdgeValidator()
    {
        // FR-17: 両端の文書 ID は必須（空 Guid は不可）。
        // **元は 1 本の `||` である。** 2 本の RuleFor へ割ると、起点だけが空のときと
        // 終点だけが空のときで違反の件数が変わる（`Errors[0]` は同じだが、件数を見る試験が
        // 将来書かれたときに移送前と食い違う）。**1 本の述語のまま写す。**
        RuleFor(r => r)
            .Must(r => r.SourceDocumentId != Guid.Empty && r.TargetDocumentId != Guid.Empty)
            .WithMessage(DocumentIdRequiredMessage);

        // FR-17: 自分自身への辺は張らせない。
        RuleFor(r => r)
            .Must(r => r.SourceDocumentId != r.TargetDocumentId)
            .WithMessage(SelfEdgeNotAllowedMessage);
    }
}
