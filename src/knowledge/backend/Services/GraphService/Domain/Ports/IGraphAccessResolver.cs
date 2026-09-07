using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Authz;

namespace GraphService.Domain.Ports;

// FR-17, FR-05, FR-21, UC-10, ADR-0036 D-07, IADR-0253 決定 5, IADR-0272 決定 4:
// 要求元の利用者属性から、**指定したアクションの** ABAC 許可スコープを解決する。
//
// 🔴 **action に既定値を置かない。** #993 は「/authz/scope が read を返すこと」を暗黙の前提にした
// 呼び出しが書き込み経路へ効いていた欠陥である。既定値を残すと、新しい経路を足した人が
// **書き忘れることで認可が緩む**。既定値を外せば、アクションの選択がコンパイラに強制される
// （既定値の不在は GraphTypeGateArchitectureTests がリフレクションで固定する）。
public interface IGraphAccessResolver
{
    // 🔴 **多重定義を作らない。** `GraphTypeGateArchitectureTests` が
    // `GetMethod(nameof(ResolveAsync))` で引いており、同名の多重定義は
    // `AmbiguousMatchException` になって既定値の不在を固定する試験が落ちる。
    // 本文で受け取った文脈から解決する口は `ResolveForUserAsync` という別名にしてある。
    Task<AccessScopeResponse> ResolveAsync(HttpContext ctx, string action, CancellationToken ct = default);

    // FR-05, FR-17, UC-10, ADR-0034 決定 1, 計画 ADR-0086 決定 1, [[IADR-0410]] (#1255):
    // **利用者文脈を本文で受け取る east-west 面（gRPC）のための解決口。**
    //
    // 🔴 **判定の位置は動かない。** 上の `ResolveAsync` と**同じ後段**（`AuthzScope/Resolve`）を
    // 同じ引数で呼ぶ —— 違うのは利用者文脈の**出所**（検証済みの `HttpContext.User` か、
    // 呼び出し元が本文で主張した値か）だけである。ホップごと ABAC の判定は依然として
    // 本サービスが行う（`ADR-0034` 決定 1）。
    //
    // 🔴 **呼び出し元の主張をそのまま評価する構造は `ADR-0086` 決定 4 が受け入れたリスクとして
    // 記録している。** 本メソッドはその形を新設したのではなく、`AuthzScope/Resolve` が
    // 既に持っていた形を east-west のもう 1 段へ広げたものである。
    Task<AccessScopeResponse> ResolveForUserAsync(
        GraphUserContext user, string action, CancellationToken ct = default);
}

// FR-05, FR-17, UC-10, ADR-0004, 計画 ADR-0086 決定 1, [[IADR-0410]] (#1255):
// ABAC 判定の**入力**としての利用者文脈。**判定結果（スコープ）ではない** ——
// `ADR-0086` の 2026-09-07 追記が「判定結果を運ぶ形は採らない」と明示的に退けている。
//
// 🔴 **`IsAuthenticated` を持つのは、未認証の短絡を輸送の手前に置くためである**（[[IADR-0335]] 決定 4）。
// 未認証なら認可サービスへ 1 度も問い合わせず deny で返す —— `AbacEvaluator` は
// 利用者条件を持たないポリシーを全利用者にマッチさせるため、匿名で問い合わせると
// そのようなポリシーが 1 件でも active なら**匿名にも許可が下りる**。
public sealed record GraphUserContext(
    string UserId,
    IReadOnlyDictionary<string, string> Attributes,
    bool IsAuthenticated)
{
    // 匿名でも到達し得る要求へ与える身元。**認可サービスへは渡らない**（この値で問い合わせない）。
    public const string AnonymousUserId = "anonymous";

    public static GraphUserContext Anonymous { get; } =
        new(AnonymousUserId, new Dictionary<string, string>(), false);

    // 検証済みの `HttpContext.User` から組み立てる（REST 経路の入口）。
    public static GraphUserContext FromHttpContext(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated != true)
            return Anonymous;

        return new GraphUserContext(
            ctx.User.Identity.Name ?? AnonymousUserId, ExtractUserAttributes(ctx), true);
    }

    // FR-05, ADR-0080, IADR-0411 (#1323): 抽出はプラットフォーム唯一の点へ委譲する。
    // 🔴 **ここで読むキーを列挙しない。** 同じ列挙が 6 か所に散っていたことが #1323 の欠陥であり、
    // 1 か所でも取り残すとその経路だけ判定が変わる。集合値（`tags` / `projects`）の符号化も
    // 共有点が持つ（`UserAttributeEncoding`）。
    private static Dictionary<string, string> ExtractUserAttributes(HttpContext ctx)
        => BffScopeResolver.ExtractUserAttributes(ctx);
}

// FR-05, FR-21, IADR-0272 決定 4: 本サービスが解決するアクションの語彙。
//
// **値域の正本は AuthorizationService の `PolicyAction`**（read / analyze / manage / write）だが、
// knowledge → platform のサービス参照は禁止であり、契約 DTO（Platform.Shared.Contracts）は
// 値域を持たない（既定値のリテラル "read" だけを持つ）。したがってここに写しを置く。
//
// **綴りがずれても緩む向きには壊れない** —— /authz/scope は値域外を 400 で返し、
// GraphAccessResolver は非 2xx を Granted=false へ縮退させる（deny 側に倒れる）。
public static class GraphAccessAction
{
    // 閲覧・探索・到達可能性の検証（ADR-0034 決定 8 は「閲覧権限を検証する」と定める）。
    public const string Read = "read";

    // 変更（ADR-0036 D-07: doc.owner ∈ { ${current_user} }）。
    public const string Write = "write";
}
