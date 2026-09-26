namespace DataSourceService.Domain.Ports;

// FR-05, UC-04, SC-06, 計画 ADR-0115 決定 1・5, ADR-0074 決定 4, [[IADR-0472]] (#1557):
// **部門コードの値域**（realm の `/department/<code>` の `<code>` の集合）の読み口。
//
// ■ なぜ `IPlatformUserDirectory` に混ぜないのか
//   問いが違う（利用者の実在 / 部門コードの値域）うえ、縮退の配線も違う —— 本ポートには REST の兄弟実装が無く、
//   gRPC 宛先が未宣言の配備では「引けなかった」に倒れる（[[IADR-0472]] 決定 2）。1 つのポートに混ぜると、
//   片方の輸送だけを持つ実装が要らない口を空実装で埋めることになる。
//
// ■ 🔴 **「値域の外」と「引けなかった」を型で分ける**（`PlatformUserDirectorySnapshot` と同じ理由）
//   空集合を返して済ませると、認可サービスが落ちている間の明示部門の書き込みがすべて「値域の外」になる。
//   **どちらも保存しない点では安全側だが、報告は嘘になる。**
public interface IDepartmentDomainDirectory
{
    /// <summary>
    /// 送った部門コードのうち、**値域に在るもの**を返す。引けなかったときは
    /// <see cref="DepartmentDomainSnapshot.Available"/> が false。照合は**序数一致**。
    /// </summary>
    Task<DepartmentDomainSnapshot> LookupAsync(IReadOnlySet<string> codes, CancellationToken ct);
}

// FR-05, SC-06, [[IADR-0472]] (#1557): 値域の断面。**`Available=false` は「値域が空」ではない。**
// `Codes` は**要求したコードのうち値域に在る部分集合**である（部門の一覧ではない）。
public sealed record DepartmentDomainSnapshot(bool Available, IReadOnlySet<string> Codes)
{
    public static DepartmentDomainSnapshot Unavailable { get; }
        = new(false, new HashSet<string>(StringComparer.Ordinal));

    public static DepartmentDomainSnapshot Of(IEnumerable<string> codes)
        => new(true, new HashSet<string>(codes, StringComparer.Ordinal));
}
