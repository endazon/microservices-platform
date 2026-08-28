using System.Reflection;
using AwesomeAssertions;
using GraphService.Domain;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Tests;

// FR-17, UC-10, ADR-0034 決定 1, IADR-0242 決定 2: **型ゲートが回避不能であることを固定する。**
//
// ADR-0034 決定 1 は「ホップごとに ABAC 判定を行う。終端でまとめてフィルタしない」を定める。
// 本リポジトリの認可 API はフィルタ集合しか返さないため、素朴に書くと「探索してから濾す」形に
// なり、それが情報漏れの経路になる。IADR-0242 決定 2 は 2 段の型ゲートでこれを表現不能にした。
//
// 🔴 **本テストが守っているのは「ゲートが後から緩められないこと」である。**
// コンストラクタを public に上げる・スコープを取らないファクトリを足す、といった変更は
// コンパイルは通ってしまうため、機械で見張る必要がある。
public class GraphTypeGateArchitectureTests
{
    // ADR-0034 決定 1: AuthorizedNode は述語を通さずに作れない。
    [Fact]
    public void AuthorizedNode_has_no_accessible_constructor()
    {
        var ctors = typeof(AuthorizedNode)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        ctors.Should().OnlyContain(c => c.IsPrivate,
            "AuthorizedNode を許可判定なしに構築できると、非許可ノードからの展開が書けてしまい "
            + "ADR-0034 決定 1（ホップごと判定）が破れる");
    }

    // ADR-0034 決定 1: AuthorizedNode を返す経路は必ずスコープを要求する。
    [Fact]
    public void Every_factory_returning_AuthorizedNode_requires_a_scope()
    {
        var factories = typeof(AuthorizedNode)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => Produces(m.ReturnType, typeof(AuthorizedNode)))
            .ToList();

        factories.Should().NotBeEmpty("構築経路が 1 つも無いと本テストが空振りする");
        factories.Should().OnlyContain(
            m => m.GetParameters().Any(p => p.ParameterType == typeof(AccessScopeResponse)),
            "スコープを取らない構築経路があると、許可判定を経ない AuthorizedNode が作れる");
    }

    // ADR-0034 決定 2: 応答 DTO は Seal 以外から作れない。
    [Fact]
    public void GraphViewResponse_has_no_accessible_constructor()
    {
        var ctors = typeof(GraphViewResponse)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        ctors.Should().OnlyContain(c => c.IsPrivate,
            "応答 DTO を直接構築できると、未フィルタの結果を直列化できてしまう");
    }

    [Fact]
    public void Every_factory_returning_GraphViewResponse_requires_a_scope()
    {
        var factories = typeof(GraphViewResponse)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => Produces(m.ReturnType, typeof(GraphViewResponse)))
            .ToList();

        factories.Should().NotBeEmpty("構築経路が 1 つも無いと本テストが空振りする");
        factories.Should().OnlyContain(
            m => m.GetParameters().Any(p => p.ParameterType == typeof(AccessScopeResponse)),
            "スコープを取らない構築経路があると、濾していない結果を Seal できてしまう");
    }

    // IADR-0242 決定 2: 未フィルタの部分グラフは公開型であってはならない
    // （公開されるとアセンブリの外＝BFF や他サービスへそのまま渡せてしまう）。
    [Fact]
    public void UnfilteredSubgraph_is_not_public()
    {
        typeof(UnfilteredSubgraph).IsPublic.Should().BeFalse(
            "未フィルタの部分グラフが公開型だと、アセンブリの外へ濾さずに渡せてしまう");
    }

    // 🔴 FR-05, FR-21, IADR-0272 決定 4 (#993): **解決アクションに既定値を置かせない。**
    //
    // #993 は「/authz/scope が read を返すこと」を暗黙の前提にした呼び出しが書き込み経路へ効いて
    // いた欠陥である。既定値を復活させると、**新しい経路を足した人が書き忘れることで認可が緩む**。
    // 既定値が無ければ、アクションの選択がコンパイラに強制される。
    [Fact]
    public void GraphAccessResolver_action_parameter_has_no_default_value()
    {
        var method = typeof(GraphService.Domain.Ports.IGraphAccessResolver)
            .GetMethod(nameof(GraphService.Domain.Ports.IGraphAccessResolver.ResolveAsync));

        method.Should().NotBeNull();
        var action = method!.GetParameters().SingleOrDefault(p => p.Name == "action");
        action.Should().NotBeNull("書き込み経路が read で判定されないよう、アクションは引数で渡す");
        action!.HasDefaultValue.Should().BeFalse(
            "既定値を置くと『書かなければ read』になり、書き忘れが認可の緩みとして現れる（#993）");
    }

    // 戻り値が T そのもの、または Nullable/コレクション越しに T を運ぶ経路も拾う。
    private static bool Produces(Type returnType, Type target)
    {
        if (returnType == target)
            return true;
        if (returnType.IsGenericType && returnType.GetGenericArguments().Contains(target))
            return true;
        return false;
    }
}
