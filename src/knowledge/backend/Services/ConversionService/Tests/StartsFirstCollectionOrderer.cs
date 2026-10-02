using Xunit.Sdk;
using Xunit.v3;

[assembly: TestCollectionOrderer(typeof(ConversionService.Tests.StartsFirstCollectionOrderer))]

namespace ConversionService.Tests;

// NFR, #1686（IADR-0491）: 実時間を長く待つ試験のコレクションを**最初に**走らせる順序づけ。
//
// xUnit v3 の既定はコレクションを無作為な順に並べ、並列の枠（既定はコア数）が空いた順に起動する。
// 実時間を最も長く待つ試験（`ExternalProcessTimeoutTests.DetachedGrandchild`。期限 ＋ 刈り取りの上限 ≒ 12 秒）が
// 後ろに回ると、他の試験が終わった後にそれだけが走り、全体の所要時間が「開始の遅れ ＋ 12 秒」に伸びる
// （実測: 同じビルドで 12.8 〜 16.7 秒に揺れ、長いほうは開始が 4.6 秒遅れていた）。
//
// 並べ替えるのは**起動の順だけ**である。既定の無作為な順をそのまま取り、`[StartsFirst]` の付いた
// コレクション定義のものを先頭へ寄せる（それ以外の相対順は既定のまま）。どの試験も走ること・何を検査するかは変えない。
public sealed class StartsFirstCollectionOrderer : ITestCollectionOrderer
{
    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(
        IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : ITestCollection =>
        DefaultTestCollectionOrderer.Instance.OrderTestCollections(testCollections)
            .OrderBy(collection => StartsFirst(collection) ? 0 : 1)
            .ToArray();

    private static bool StartsFirst(ITestCollection collection) =>
        collection is IXunitTestCollection { CollectionDefinition: { } definition }
        && definition.IsDefined(typeof(StartsFirstAttribute), inherit: false);
}

// コレクション定義（`[CollectionDefinition]`）に付けて、そのコレクションを最初に起動させる印。
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class StartsFirstAttribute : Attribute;
