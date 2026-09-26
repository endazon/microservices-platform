using DataSourceService.Domain.Ports;

namespace DataSourceService.Tests;

// FR-05, UC-04, SC-06, 計画 ADR-0115 決定 1・5, [[IADR-0472]] (#1557): 部門コードの値域のテスト実装。
//
// 既定の値域は開発用 realm の部門グループ（`/department/engineering` / `sales` / `hr`）と同じにする
// （`deploy/keycloak/microservices-platform-realm.json`）。既存の試験が明示する部門（`sales` / `hr`）が値域に在るので、
// 本作業の前から在る試験は挙動を変えない。
//
// 🔴 **`Available` を切り替えられることが要件である**（「値域の外」400 と「引けなかった」502 を作り分ける）。
public sealed class StubDepartmentDomainDirectory : IDepartmentDomainDirectory
{
    public HashSet<string> Codes { get; } = new(StringComparer.Ordinal) { "engineering", "sales", "hr" };

    public bool Available { get; set; } = true;

    // 何回引かれたか。**予約値・空白・未指定・`defaultAttributes` を送らない PATCH が値域を引かない**ことを固定する。
    public int CallCount { get; private set; }

    public IReadOnlySet<string> LastQuery { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    public void Reset()
    {
        Codes.Clear();
        Codes.UnionWith(["engineering", "sales", "hr"]);
        Available = true;
        CallCount = 0;
        LastQuery = new HashSet<string>(StringComparer.Ordinal);
    }

    public Task<DepartmentDomainSnapshot> LookupAsync(IReadOnlySet<string> codes, CancellationToken ct)
    {
        CallCount++;
        LastQuery = codes;
        return Task.FromResult(Available
            // 🔴 要求したコードとの交差を返す（実装と同じ形。全件を返すと照会していないコードまで「在る」になる）。
            ? DepartmentDomainSnapshot.Of(Codes.Where(codes.Contains))
            : DepartmentDomainSnapshot.Unavailable);
    }
}
