using FluentValidation;

namespace McpServer.Features.McpClients.RegisterClient;

// FR-16, UC-09 基本フロー 1, SC-12, 計画 ADR-0024 / ADR-0030 §決定（検証 = FluentValidation）/
// IADR-0371 決定 2 / IADR-0395 決定 2 / [[IADR-0398]] 決定 1 (b)・5・9:
// クライアント登録の入力規則。従前は `Endpoint.cs` 内の手書きガード節 3 本であった。
//
// 🔴 **この検証器は鍵を持たない**（`OverridePropertyName` を書かない）。
// 応答の鍵（`request`）は sink（`McpClientEndpoints.Problem`）が持っており、
// **鍵の正を 2 つにしないため**である（[[IADR-0398]] 決定 1 (b)）。
// DocumentService（PR-A / PR-B）は逆に `OverridePropertyName` で**必ず**鍵を明示した ——
// あちらはサイトごとに鍵が違い、鍵の正が検証器にしか無いからである。**作法が違うのは前提が違うため**で、
// どちらも「鍵の正は 1 つ」という同じ規約の適用である。推論名（`ClientId` / `Kind` / `EgressTier`）は
// `PropertyName` に入るが**応答本文には出ない**。それを `McpValidationProblemContractTests` が固定する。
//
// 🔴 **宣言順が応答の契約である。** 移送前は `clientId` → `kind` → `egressTier` の順にガード節が並び、
// **最初の違反でその場で `return` していた**（形 α）。3 つとも不正な要求の応答は
// `{"errors":{"request":["clientId は必須です。"]}}` の 1 鍵 1 件である。端点は `Errors[0]` だけを載せる。
//
// 🔴 **検証と解析を分け、対応表は 1 つに保つ**（[[IADR-0398]] 決定 5）。
// `RegisterMcpClientEndpoint.TryParseKind` / `TryParseTier` を**そのまま呼ぶ** —— 自前の集合比較を
// 書くと、`Trim().ToLowerInvariant()`（`" Service-Account "` は有効）と
// 「`egressTier` 未指定は有効（既定 = standard-external）」がここで割れ、
// **「検証は通るが解析で落ちる」あるいはその逆**が生まれる。
internal sealed class RegisterMcpClientValidator : AbstractValidator<RegisterMcpClientRequest>
{
    // FR-16: 移送前のガード節が返していた本文。**この文言が応答の契約である。**
    internal const string ClientIdRequiredMessage = "clientId は必須です。";

    // 🔴 補間を含むので `const` にできない。移送前と**同じ式**から作る（`Endpoint.cs:20-21`）。
    internal static string KindInvalidMessage(string? kind)
        => $"kind の値 '{kind}' は不正です（interactive / service-account）。";

    // 同上（`Endpoint.cs:23`）。
    internal static string EgressTierInvalidMessage(string? egressTier)
        => $"egressTier の値 '{egressTier}' は不正です。";

    public RegisterMcpClientValidator()
    {
        // 🔴 述語を写す。`IsNullOrWhiteSpace` を `NotEmpty()` へ置き換えない
        // （`NotEmpty()` は既定値も落とすので意味が広がる）。
        RuleFor(r => r.ClientId)
            .Must(v => !string.IsNullOrWhiteSpace(v))
            .WithMessage(ClientIdRequiredMessage);

        RuleFor(r => r.Kind)
            .Must(v => RegisterMcpClientEndpoint.TryParseKind(v, out _))
            .WithMessage(r => KindInvalidMessage(r.Kind));

        RuleFor(r => r.EgressTier)
            .Must(v => RegisterMcpClientEndpoint.TryParseTier(v, out _))
            .WithMessage(r => EgressTierInvalidMessage(r.EgressTier));
    }
}
