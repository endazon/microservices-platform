using Platform.Shared.Contracts.Dtos;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace Platform.Shared.Infrastructure.Foundation.Llm;

// FR-02, FR-03, FR-05, ADR-0016, ADR-0029, ADR-0075, IADR-0379, IADR-0397 (#1255):
// LlmGateway の REST DTO ↔ proto の**写像だけ**を置く。
//
// 🔴 **ここに置いてよいのは写像だけである。** キャッシュ・タイムアウト・リトライ・fail-safe は
// 呼び出し元サービスの Infrastructure に置く（ADR-0029 2026-08-04 追記）。呼び出し元ごとに縮退の
// 落とし先が違う（Retrieval は空ベクトル、Ingestion は Retryable 経由で再試行）ため、
// ここへ寄せると 1 つの縮退規則をすべての呼び出し元へ押しつけることになる。
//
// 呼び出し元と呼び出し先が**同じ写像**を使う（gRPC 面のサーバもこれを呼ぶ）ので、
// 「送る側と受ける側で項目名が 1 つずれる」形の壊れ方（#992 で実際に踏んだ形）が起こらない。
public static class LlmGrpcMapping
{
    // 🔴 proto3 に null は無い（IADR-0397 決定 3）。EMBED_PURPOSE_UNSPECIFIED（proto3 の既定 0）は
    // REST の DTO 既定である **Index** へ写す。Query 以外はすべて Index（REST 側の
    // `req.Purpose == EmbedPurpose.Query ? Query : Index` と同じ倒し方）。
    public static EmbedPurpose ToDtoPurpose(Pb.EmbedPurpose purpose) =>
        purpose == Pb.EmbedPurpose.Query ? EmbedPurpose.Query : EmbedPurpose.Index;

    public static Pb.EmbedPurpose ToProtoPurpose(EmbedPurpose purpose) =>
        purpose == EmbedPurpose.Query ? Pb.EmbedPurpose.Query : Pb.EmbedPurpose.Index;

    public static Pb.EmbedRequest ToProto(EmbedApiRequest req) => new()
    {
        Text = req.Text,
        // proto3 の string は null を持てない。REST の null（未指定）は空文字へ写し、
        // 受け側の SensitivityClasses.Parse が null と同じ restricted（安全側）へ倒す。
        Confidentiality = req.Confidentiality ?? string.Empty,
        Purpose = ToProtoPurpose(req.Purpose),
    };

    public static Pb.EmbedResponse ToProto(EmbedApiResponse resp)
    {
        var proto = new Pb.EmbedResponse
        {
            Dimensions = resp.Dimensions,
            Model = resp.Model,
            Collection = resp.Collection,
            Embedded = resp.Embedded,
            Endpoint = resp.Endpoint ?? string.Empty,
            RoutingReason = resp.RoutingReason ?? string.Empty,
            Retryable = resp.Retryable,
        };
        proto.Vector.AddRange(resp.Vector);
        return proto;
    }

    public static EmbedApiResponse ToDto(Pb.EmbedResponse resp) => new(
        Vector: [.. resp.Vector],
        Dimensions: resp.Dimensions,
        Model: resp.Model,
        Collection: resp.Collection,
        Embedded: resp.Embedded,
        // 空文字は REST の null（未設定）へ戻す。REST 応答の JSON でも null で届く項目である。
        Endpoint: string.IsNullOrEmpty(resp.Endpoint) ? null : resp.Endpoint,
        RoutingReason: string.IsNullOrEmpty(resp.RoutingReason) ? null : resp.RoutingReason,
        Retryable: resp.Retryable);

    // ---- テキスト生成（FR-04, FR-11, ADR-0010, IADR-0398 (#1255)）----------------------------------
    //
    // 写像だけを置く規則は埋め込みと同じである（上の 🔴）。**呼び出し元と呼び出し先が同じ写像を使う**
    // ので、「送る側と受ける側で項目名が 1 つずれる」形の壊れ方が起こらない。

    /// <summary>
    /// REST の <c>CompletionApiRequest</c> 既定を proto3 の「未指定」へ写す（クライアント側）。
    /// proto3 の string は null を持てないため、REST の null はすべて空文字にする ——
    /// 受け側が空文字を REST の null と同じに扱うことは <see cref="ToDto(Pb.CompleteRequest)"/> の
    /// 注記と GrpcCompleteTests が固定する。
    /// </summary>
    public static Pb.CompleteRequest ToProto(CompletionApiRequest req) => new()
    {
        Prompt = req.Prompt,
        MaxTokens = req.MaxTokens,
        Model = req.Model ?? string.Empty,
        Confidentiality = req.Confidentiality ?? string.Empty,
        Purpose = req.Purpose ?? string.Empty,
    };

    /// <summary>
    /// proto の要求を REST の DTO へ写す（サーバ側）。
    /// 🔴 <b>proto3 に null は無い</b>（IADR-0398 決定 4）。REST の既定値のうち、
    /// <c>max_tokens</c> だけは <b>0 が「未指定」と区別できない</b>ため、ここで明示的に写す。
    /// <para>
    /// <c>model</c> / <c>confidentiality</c> / <c>purpose</c> の空文字は写し不要である ——
    /// 受け側（<c>LlmRouter</c> の <c>IsNullOrWhiteSpace</c>・<c>SensitivityClasses.Parse</c>・
    /// <c>CompletionUseCase</c> の <c>IsNullOrWhiteSpace ? "default"</c>）が
    /// <b>null と空文字を同じに扱う</b>ことを実測して確かめてある。ここで null へ戻すと
    /// 「写しが 2 か所にある」状態になり、片方だけが直る事故の口になる。
    /// </para>
    /// </summary>
    public static CompletionApiRequest ToDto(Pb.CompleteRequest req) => new(
        Prompt: req.Prompt,
        // 🔴 0 は「未指定」であり「0 トークン」ではない。REST の既定（IADR-0101 の 4096）へ写す。
        // 写し漏れは例外にならず、**プロバイダが max_tokens=0 を受け取って本文が空になる**
        // 形で静かに壊れる（thinking が既定で有効なモデルでは特に）。
        MaxTokens: req.MaxTokens == 0 ? DefaultMaxTokens : req.MaxTokens,
        Model: req.Model,
        Confidentiality: req.Confidentiality,
        Purpose: req.Purpose);

    /// <summary>
    /// REST の <c>CompletionApiRequest.MaxTokens</c> の既定（IADR-0101）。
    /// DTO の既定引数はリフレクションでしか読めないため、写しの側で名前を付けて 1 か所に置く。
    /// </summary>
    public const int DefaultMaxTokens = 4096;

    public static Pb.CompleteResponse ToProto(CompletionApiResponse resp) => new()
    {
        Text = resp.Text,
        Model = resp.Model,
        InputTokens = resp.InputTokens,
        OutputTokens = resp.OutputTokens,
        // 🔴 明示的に写す（DTO の既定は true・proto3 の既定は false で向きが逆）。
        Sent = resp.Sent,
        Endpoint = resp.Endpoint ?? string.Empty,
        RoutingReason = resp.RoutingReason ?? string.Empty,
        StopReason = resp.StopReason ?? string.Empty,
    };

    public static CompletionApiResponse ToDto(Pb.CompleteResponse resp) => new(
        Text: resp.Text,
        Model: resp.Model,
        InputTokens: resp.InputTokens,
        OutputTokens: resp.OutputTokens,
        Sent: resp.Sent,
        // 空文字は REST の null（未設定）へ戻す。REST 応答の JSON でも null で届く項目である。
        Endpoint: string.IsNullOrEmpty(resp.Endpoint) ? null : resp.Endpoint,
        RoutingReason: string.IsNullOrEmpty(resp.RoutingReason) ? null : resp.RoutingReason,
        StopReason: string.IsNullOrEmpty(resp.StopReason) ? null : resp.StopReason);

    /// <summary>
    /// SSE の 1 イベント（<c>CompletionStreamEvent</c>）を proto のメッセージへ写す。
    /// <para>
    /// 🔴 <b><c>Sent</c> は proto3 の既定と向きが逆である</b>（DTO 既定 <c>true</c> ／ proto3 既定
    /// <c>false</c>。IADR-0398 決定 4）。delta メッセージにも <c>sent=true</c> を明示的に書く ——
    /// 落とすと例外にはならず、<b>全 delta が「縮退」に見える</b>形で静かに壊れる
    /// （呼び出し元は縮退表示・提案 0 件・画像保持へ倒れる）。GrpcCompleteStreamTests が固定する。
    /// </para>
    /// </summary>
    public static Pb.CompletionStreamEvent ToProto(CompletionStreamEvent ev) => new()
    {
        Delta = ev.Delta,
        Done = ev.Done,
        Sent = ev.Sent,
        Text = ev.Text ?? string.Empty,
        Model = ev.Model,
        InputTokens = ev.InputTokens,
        OutputTokens = ev.OutputTokens,
        RoutingReason = ev.RoutingReason ?? string.Empty,
        StopReason = ev.StopReason ?? string.Empty,
    };

    public static CompletionStreamEvent ToDto(Pb.CompletionStreamEvent ev) => new(
        Delta: ev.Delta,
        Done: ev.Done,
        Sent: ev.Sent,
        Text: string.IsNullOrEmpty(ev.Text) ? null : ev.Text,
        Model: ev.Model,
        InputTokens: ev.InputTokens,
        OutputTokens: ev.OutputTokens,
        RoutingReason: string.IsNullOrEmpty(ev.RoutingReason) ? null : ev.RoutingReason,
        StopReason: string.IsNullOrEmpty(ev.StopReason) ? null : ev.StopReason);
}
