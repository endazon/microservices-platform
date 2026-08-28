using Platform.Shared.Contracts.Dtos;

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
    Task<AccessScopeResponse> ResolveAsync(HttpContext ctx, string action, CancellationToken ct = default);
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
