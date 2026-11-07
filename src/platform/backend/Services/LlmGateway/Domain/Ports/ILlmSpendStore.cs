namespace LlmGateway.Domain.Ports;

// NFR-18, NFR-21, ADR-0044, IADR-0698: 当日の費用の枠（呼び手・呼び手×用途ごとの UTC の当日の合計）の置き場。
//
// 上限の判定器（LlmCallLimiter）が、見込み額を予約した直後と、実額へ置き換えた・戻した直後に、その日の値を丸ごと置く。
// 🔴 **読めない・書けないときは例外を投げる。** 判定器はそれを受けて段 2 では送らない側へ倒す（fail-closed）。
// 黙って空を返す実装にしない（空は「前回の消費が 0」と区別できず、枠が 0 に戻る）。
public interface ILlmSpendStore
{
    // 真なら再起動を越えて残る。偽ならプロセス内だけ（再起動で当日の枠が 0 に戻る）。
    bool IsDurable { get; }

    // 前回置いた値。一度も置いていなければ null。読めなければ例外。
    LlmSpendSnapshot? Load();

    // その日の値を丸ごと置き換える。置けなければ例外。
    void Save(LlmSpendSnapshot snapshot);
}

// その日（UTC）の鍵ごとの合計。鍵は判定器の枠の鍵（`c|<呼び手>`・`p|<呼び手>|<用途>`）。
public sealed record LlmSpendSnapshot(DateOnly Day, IReadOnlyDictionary<string, decimal> Totals);
