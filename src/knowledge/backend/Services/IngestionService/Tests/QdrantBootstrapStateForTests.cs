using IngestionService.Common.Concurrency;

namespace IngestionService.Tests;

// FR-02, [[IADR-0650]] (#2283): 受け口・後続の器は保証の成功（`QdrantBootstrapState`）を必須の引数で受ける。
// 保証そのものを測らない試験は、成功済みの状態を渡す。
internal static class QdrantBootstrapStateForTests
{
    internal static QdrantBootstrapState Ensured()
    {
        var state = new QdrantBootstrapState();
        state.MarkEnsured();
        return state;
    }
}
