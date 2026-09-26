namespace DocumentService.Domain.Ports;

// FR-20, SC-17, UC-11, NFR-14, 計画 ADR-0114 決定 1〜3, ADR-0037 フォローアップ 3,
// ADR-0096 フォローアップ 2, [[IADR-0474]] (#1532):
// **同期トークンの所有者のアカウントが、いま有効か**を基盤の利用者名簿へ訊く口。
//
// ■ なぜ同期要求ごとに訊くのか（案 A）
//   無効化は Keycloak の `enabled=false` として基盤にだけ記録され、同期トークン（端末）には触れない。
//   🔴 **platform → knowledge の依存は禁止**なので、無効化の端点から端末を失効させには行けない。
//   knowledge → platform の向き（既存の狭い読み口 `UserDirectory/GetUserAttributes`）で答えを取りに行く。
//   🔴 **キャッシュしない** —— ADR-0114 決定 1 は「無効化の後の**最初の**同期要求」から拒否せよと定める。
//
// ■ 退職の窓の口（IOwnerRetentionDirectory）と分けた理由
//   同じ rpc を読むが、**倒す向きが逆である**。あちらは「分からなければ消さない」、こちらは
//   「分からなければ通さない」（ADR-0114 決定 2）。1 つの口に 2 つの述語を載せると、
//   試験の既定値（あちらは「引けなかった」）も縮退の意味も片方に引きずられる。
public interface IOwnerAccountDirectory
{
    /// <summary>
    /// 所有者 1 人のアカウントの状態を返す。
    /// 🔴 **引けなかった・時間切れ・未構成は <see cref="OwnerAccountState.Unknown"/>**（例外にしない）。
    /// 呼び出し元は <see cref="OwnerAccountState.Enabled"/> 以外をすべて拒否する。
    /// </summary>
    Task<OwnerAccountState> GetStateAsync(string ownerId, CancellationToken ct);
}

// FR-20, ADR-0114 決定 1・2, [[IADR-0474]] (#1532): アカウントの状態。
// 🔴 **`Unknown` を 0 に置く** —— 既定値（初期化漏れ・未知の値の写し損ね）が「通す」へ倒れない。
// 🔴 **bool にしない** —— `false` が「無効化された」と「分からなかった」を畳み、ログと試験で区別できなくなる。
public enum OwnerAccountState
{
    /// <summary>判定できない（名簿を読めない・時間切れ・口が未構成）。**通さない**（ADR-0114 決定 2）。</summary>
    Unknown = 0,

    /// <summary>名簿に居て有効。**同期を通してよい唯一の値**である。</summary>
    Enabled,

    /// <summary>名簿に居るが無効化されている。通さない（ADR-0114 決定 1）。</summary>
    Disabled,

    /// <summary>名簿に居ない（削除された等）。通さない —— 有効だと確かめられないからである。</summary>
    NotFound,
}
