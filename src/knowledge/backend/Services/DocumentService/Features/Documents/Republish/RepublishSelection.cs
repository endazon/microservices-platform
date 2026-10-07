using DocumentService.Features.Documents.ListPage;
using Knowledge.Contracts.Dtos;

namespace DocumentService.Features.Documents.Republish;

// FR-02, FR-06, UC-04, ADR-0013, [[IADR-0503]] 決定 2 (#1762): 再発行の対象の選び方（純粋関数）。
//
// 🔴 **並びは `GET /documents/page` と同じ「作成時刻昇順・同時刻は ID 昇順」のキーセットである**（`DocumentPageCursor` を再利用）。
// 並びのキーが不変なので、走査の間ずっと在った文書はちょうど 1 回ずつ選ばれる。途中で作られた文書は末尾に現れ、
// 呼び出し側が `createdBefore` を走査の開始時刻に固定すれば選ばれない（それらは作成の経路で既に発行されている）。
// 削除はカーソルを動かさない。**更新時刻で並べない** —— 走査の途中で更新された文書が前へ移り、読み飛ばされる。
// ［2026-10-08 / #1765 / [[IADR-0509]]］属性の絞り込みが無いときの並び・カーソルの比較は SQL で行う
// （`DocumentPageQuery.AfterCursor` / `InPageOrder`）。下の `Order` / `IsAfter` / `Remaining` は属性の絞り込みがある経路だけが使う
// （その経路の中では並べる・比べるがどちらも .NET なので、規則は経路の中で閉じている）。`Slice` と `Summarize` は両方の経路が使う。
//
// 🔴 **個人資料を除かない。** 露出のトグルが ON の個人資料は索引に載るべき文書であり、除くと再索引から漏れる。
// 載せるかどうかは発行の門（`DocumentEndpoints.PassesPublishGate`）が決める —— 判定を 2 か所に割らない。
internal static class RepublishSelection
{
    internal const int DefaultLimit = 100;
    internal const int MaxLimit = 500;
    internal const int MaxIds = 500;
    internal const int MaxRequestedByLength = 100;
    internal const int MaxReasonLength = 500;

    // 台帳から絞り込みに要る列だけを投影した行（本体は選んだページの分だけ読み直す）。
    internal sealed record Row(Guid Id, DateTimeOffset CreatedAt,
        IReadOnlyDictionary<string, string> Attributes, bool HasMarkdownUri);

    internal static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    // 属性の完全一致（AND。キー・値とも大文字小文字を区別する）。`GET /documents/page` の `attr.` と同じ意味。
    internal static bool MatchesAttributes(IReadOnlyDictionary<string, string> attributes,
        IReadOnlyDictionary<string, string>? filters)
        => filters is null || filters.All(f =>
            attributes.TryGetValue(f.Key, out var v) && string.Equals(v, f.Value, StringComparison.Ordinal));

    // 絞り込みに一致した行を全順序で並べる（カーソルに依らない母集合 = 応答の `matched`）。
    internal static List<Row> Order(IEnumerable<Row> rows, IReadOnlyDictionary<string, string>? filters)
        => [.. rows
            .Where(r => MatchesAttributes(r.Attributes, filters))
            .OrderBy(r => r.CreatedAt.UtcTicks)
            .ThenBy(r => r.Id)];

    // 並びでカーソルより後ろにあるか（`DocumentPageCursor.Precedes` と同じ比較を行に対して行う）。
    // 🔴 **厳密に後ろ**である。「以上」にすると前ページの末尾をもう一度選ぶ（重複は冪等だが件数が嘘になる）。
    internal static bool IsAfter(DocumentPageCursor? after, Row r)
    {
        if (after is null) return true;
        var ticks = r.CreatedAt.UtcTicks;
        return ticks > after.Value.CreatedAtUtcTicks
            || (ticks == after.Value.CreatedAtUtcTicks && r.Id.CompareTo(after.Value.Id) > 0);
    }

    // カーソルより後ろの残り全件（並びは保つ）。
    internal static List<Row> Remaining(List<Row> ordered, DocumentPageCursor? after)
        => [.. ordered.Where(r => IsAfter(after, r))];

    // 残りの先頭から `limit` 件を切り出し、続きがあれば**このページの末尾**を指すカーソルを返す。
    internal static (List<Row> Page, string? NextCursor) Slice(List<Row> remaining, int limit)
    {
        if (remaining.Count <= limit)
            return (remaining, null);

        var page = remaining.Take(limit).ToList();
        var last = page[^1];
        return (page, new DocumentPageCursor(last.CreatedAt.UtcTicks, last.Id).Encode());
    }

    // 内訳（門で止まる件数・本文なし・機密区分）を **1 回の走査で**数える（dry-run は残り全件を数えるので、件数ごとに列挙し直さない）。
    // 門の判定は発行の門と同じ関数（`DocumentEndpoints.PassesPublishGate`）を属性に対して使う。
    // 機密区分の内訳: **欠落・未知は安全側（restricted）へ倒す** —— 取り込みが語彙索引へ回す判定
    // （`LexicalIndexPolicy`）と同じ入力（`ConfidentialityLevels.FromAttributes`）であり、
    // `public` / `internal` の件数が埋め込みへ進む件数（費用の見積もりの母数）になる。
    // 0 件の区分も並べる（「無い」と「数えていない」を取り違えない）。
    internal static (int SkippedByGate, int WithoutBody, Dictionary<string, int> ByConfidentiality) Summarize(IEnumerable<Row> rows)
    {
        var counts = ConfidentialityLevels.All.ToDictionary(l => l, _ => 0, StringComparer.Ordinal);
        var skippedByGate = 0;
        var withoutBody = 0;
        foreach (var r in rows)
        {
            counts[ConfidentialityLevels.FromAttributes(r.Attributes)]++;
            if (!DocumentEndpoints.PassesPublishGate(r.Attributes)) skippedByGate++;
            if (!r.HasMarkdownUri) withoutBody++;
        }
        return (skippedByGate, withoutBody, counts);
    }

    // 要求の検証。**先頭の 1 件を、その鍵で返す**（他の口と同じ作法）。
    // 🔴 `dryRun` は必須である。省略を「発行する」に倒すと、確かめるつもりの呼び出しが 22,564 件を流す。
    internal static (string Key, string Message)? Validate(RepublishDocumentUpdatedRequest req)
    {
        if (req.DryRun is null)
            return ("dryRun", "dryRun を明示してください（true で件数と内訳だけを返し、false で発行します）。");
        // 🔴 **空の `ids` は「全件」ではない。** 絞り込みの指定が空になった呼び出し（組み立ての誤り）を全件の発行に倒さない。
        if (req.Ids is { Count: 0 })
            return ("ids", "ids が空です。全件を対象にするなら ids を省略してください。");
        if (req.Ids is { Count: > MaxIds })
            return ("ids", $"ids は {MaxIds} 件までです。");
        if (req.Attributes is not null)
        {
            foreach (var (key, value) in req.Attributes)
            {
                if (string.IsNullOrWhiteSpace(key))
                    return ("attributes", "属性キーが空です。");
                if (string.IsNullOrEmpty(value))
                    return ($"attributes.{key}", "属性の値が空です。");
            }
        }
        if (req.RequestedBy is { Length: > MaxRequestedByLength })
            return ("requestedBy", $"requestedBy は {MaxRequestedByLength} 文字までです。");
        if (req.Reason is { Length: > MaxReasonLength })
            return ("reason", $"reason は {MaxReasonLength} 文字までです。");
        if (req.Cursor is not null && !DocumentPageCursor.TryDecode(req.Cursor, out _))
            return ("cursor", "カーソルが不正です。前の応答の nextCursor をそのまま渡してください。");
        return null;
    }
}

// FR-02, FR-06, [[IADR-0503]] 決定 2 (#1762): 要求。
// `dryRun` は必須（省略は 400）。`limit` は既定 100・1〜500 に丸める。`createdBefore` はこの時刻**より前**に作られた文書だけ。
// `ids` は空を 400 にする（省略が全件）。`requestedBy` / `reason` は記録のためだけの任意の札（認証済みの主体と一緒にログへ出す。
// 長さの上限を超えたら 400。ログへは制御文字を潰してから出す）—— 口の主体は駆動スクリプトの機械の client なので、人の名前と理由はここで運ぶ。
public sealed record RepublishDocumentUpdatedRequest(
    bool? DryRun,
    int? Limit = null,
    string? Cursor = null,
    DateTimeOffset? CreatedBefore = null,
    List<Guid>? Ids = null,
    Dictionary<string, string>? Attributes = null,
    string? RequestedBy = null,
    string? Reason = null);

// FR-02, FR-06, [[IADR-0503]] 決定 2 (#1762): 応答。
// `matched` はカーソルに依らない絞り込みの全件、`remaining` はこの呼び出しの前に残っていた件数。
// 内訳（`skippedByGate`・`withoutBody`・`byConfidentiality`）は、発行ではこのページ、dry-run では残り全件について数える。
public sealed record RepublishDocumentUpdatedResponse(
    bool DryRun,
    int Matched,
    int Remaining,
    int Selected,
    int Published,
    int SkippedByGate,
    int WithoutBody,
    IReadOnlyDictionary<string, int> ByConfidentiality,
    string? NextCursor);
