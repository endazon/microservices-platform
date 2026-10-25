namespace Knowledge.Contracts.Ordering;

// 🔴 FR-02, FR-13, FR-17, FR-19, IADR-0638 決定 1 (#2150 / #2251): **文書のイベントの前後を決める唯一の点**（純関数）。
//
// 受け手（取り込み・グラフ・Wiki）は、印（最後に反映した版）と到着したイベントをこの関数で比べる。述語を受け手ごとに書き分けない
// （片方だけ直る形を作らない。`DocumentExposure` を発行側と受け手で共有するのと同じ理由）。
//
// - 鍵は辞書順 (時刻, 連番) である。時刻は `DocumentUpdated.UpdatedAt`（削除なら `DocumentDeleted.DeletedAt`）、連番は `Sequence`。
// - 🔴 **連番を第 1 の鍵にしない。** 文書 ID は再利用され（IADR-0596 決定 2。変換が ID を決定的に採番する）、作り直した文書の連番は
//   0 から始まる。連番を先にすると、作り直した文書の更新が削除の前の印より永久に古く読まれる。
// - 時刻が等しく、双方に連番があるときだけ連番で比べる。どちらかが null（旧形式の電文・移行前の印）なら時刻だけで比べる。
// - 🔴 **時刻は呼び出し側が保存の精度へ丸めてから渡す**（取り込みはミリ秒、Wiki とグラフはマイクロ秒）。丸めずに比べると、
//   同じ版の再配送が保存値より「新しい」と読め、同じ時刻の逆転（#2150 の根）を見分けられない。
// - 🔴 **`Events/` に置かない。** これはイベントではない。`scripts/check-event-topology.js` は `*.Contracts/Events/*.cs` をイベント型として数える。
public static class DocumentEventOrder
{
    // a が b より後なら正、前なら負、区別できなければ 0。
    public static int Compare(DateTimeOffset aAt, long? aSequence, DateTimeOffset bAt, long? bSequence)
    {
        var byTime = aAt.UtcTicks.CompareTo(bAt.UtcTicks);
        if (byTime != 0) return byTime;
        return aSequence is { } a && bSequence is { } b ? a.CompareTo(b) : 0;
    }

    // 到着したもの（incoming）が、反映済みの印（applied）より**厳密に古い**か。等しいもの（再配送・再発行）は古くない。
    public static bool IsOlder(DateTimeOffset incomingAt, long? incomingSequence, DateTimeOffset appliedAt, long? appliedSequence)
        => Compare(incomingAt, incomingSequence, appliedAt, appliedSequence) < 0;
}
