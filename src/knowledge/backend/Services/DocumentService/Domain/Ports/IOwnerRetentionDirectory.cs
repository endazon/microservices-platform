namespace DocumentService.Domain.Ports;

// FR-19, UC-11, SC-19, SC-17, 計画 ADR-0036 D-09, ADR-0096 決定 1・2, ADR-0057 決定 1,
// [[IADR-0428]] 決定 3, [[IADR-0431]] (#1409):
// **個人資料の所有者が退職しており、30 日の窓が閉じたか**を基盤へ訊く口。
//
// ■ なぜ判定を自分でやらないのか
//   起点（IdP の予約属性）の解決・ISO-8601 の固定書式・**期間 30 日**・「未供給／読めない」の
//   fail-safe は `AuthorizationService.Domain.RetentionAnchorPolicy` が単一情報源である
//   （[[IADR-0428]] 決定 2・3）。🔴 **knowledge ユニットは platform のサービス実装を参照できない**
//   （ユニット外参照は `src/platform/backend/Shared/` の 3 プロジェクトのみ）。
//   複写すると **30 日が 2 か所に生まれ、片方を直したときにもう片方が黙って残る** ——
//   `PrivateNote.RetentionDays = 90` と混同した実例が既にある（[[IADR-0428]] のコンテキスト）。
//   よって**答えだけを運ぶ**。
//
// ■ 🔴 削除してよいのは 1 通りだけである
//   `Found && !Enabled && Eligibility == Elapsed`。
//   それ以外（居ない・有効・まだ経っていない・数えていない・**引けなかった**）は**すべて残す**。
//   ADR-0057 決定 2 により残余を置かない＝**誤削除の救済手段が無い**（ADR-0096 §結果）ため、
//   曖昧さは常に「消さない」へ倒す。
public interface IOwnerRetentionDirectory
{
    /// <summary>
    /// 所有者 1 人の退職の窓の状態を返す。
    /// 🔴 **引けなかったときは <c>null</c>**（「窓は閉じていない」ではなく「分からない」）。
    /// 呼び出し元はこれを削除しない側へ倒す。
    /// </summary>
    Task<OwnerRetentionStatus?> GetAsync(string ownerId, CancellationToken ct);
}

// FR-19, ADR-0096 決定 1, [[IADR-0428]] 決定 3, [[IADR-0431]] (#1409): 窓の判定。
// 🔴 **bool にしない** —— `false` が「まだ経っていない」と「そもそも数えていない」を畳む
// （[[IADR-0428]] 決定 3 が退けた形をユニットを跨いでも保つ）。
public enum OwnerRetentionEligibility
{
    /// <summary>起点から 30 日が経過した。**削除してよい唯一の値**である。</summary>
    Elapsed,

    /// <summary>起点はあるが 30 日はまだ経っていない。</summary>
    WithinWindow,

    /// <summary>🔴 起点が無い／読めない／未知の値。**数えていない**（削除しない）。</summary>
    NotEvaluable,
}

// FR-19, ADR-0096 決定 1, [[IADR-0431]] (#1409): 所有者 1 人の像。
// 🔴 `Found=false`（名簿に居ない）は**エラーではない**が、**削除の根拠にもならない** ——
// 名簿の同期漏れで所有者が引けないだけかもしれず、それを「退職した」と読むと資料が消える。
public sealed record OwnerRetentionStatus(
    bool Found, bool Enabled, OwnerRetentionEligibility Eligibility)
{
    /// <summary>ADR-0096 決定 1 の述語。**ここ 1 か所だけが true を返し得る。**</summary>
    public bool IsPurgeable => Found && !Enabled && Eligibility == OwnerRetentionEligibility.Elapsed;
}
