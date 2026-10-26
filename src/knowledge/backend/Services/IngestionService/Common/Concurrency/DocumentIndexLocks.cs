namespace IngestionService.Common.Concurrency;

// 🔴 FR-02, FR-06, IADR-0640 決定 3 (#2263): **文書ごとの排他（プロセス内）。** 取り込み（`DocumentUpdated`）・削除（`DocumentDeleted`）の受け口と、
// 埋め込み待ちの点の埋め直しは、同じ文書について直列に動く。
//
// 版の印の読み書きは比較交換ではない（IADR-0595 の残余）。受け口は別々のキューで同じプロセスの中を並行に動くので、排他が無いと
// 「更新の受け口が門を通った後に書かれた墓石を、更新の受け口が自分の印で上書きし、点を書き、読み直しも自分の印を読む」形で、
// 削除した文書が索引に戻る。グラフの `pg_advisory_xact_lock`（文書ごと）に当たるものを、DB を持たない取り込みの中に置く。
// 既定の配備は 1 レプリカである。複数レプリカでは効かない（IADR-0595 の残余と同じ条件で、比較交換か分散の排他が要る）。
public sealed class DocumentIndexLocks
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }

    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Lock _sync = new();

    // 文書の排他を取る。返り値を破棄すると解ける。待ちは ct で取り消せる（受け口の実行期限が切る）。
    public async Task<IDisposable> AcquireAsync(Guid documentId, CancellationToken ct)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(documentId, out entry!))
                _entries[documentId] = entry = new Entry();
            entry.Users++;
        }

        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Leave(documentId, entry);
            throw;
        }
        return new Releaser(this, documentId, entry);
    }

    // 試験用: いま排他を持つか待っている文書の数（使い終えた文書の項目が残らないことを見る）。
    internal int TrackedDocuments
    {
        get
        {
            lock (_sync) return _entries.Count;
        }
    }

    private void Leave(Guid documentId, Entry entry)
    {
        lock (_sync)
        {
            if (--entry.Users == 0)
                _entries.Remove(documentId);
        }
    }

    private sealed class Releaser(DocumentIndexLocks owner, Guid documentId, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            entry.Gate.Release();
            owner.Leave(documentId, entry);
        }
    }
}
