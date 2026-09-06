using DataSourceService.Domain.Ports;

namespace DataSourceService.Tests;

// FR-05, UC-04, SC-06, ADR-0074 決定 4 (#1194): 利用者名簿のテスト実装。
//
// 本番の実装は AuthorizationService の /authz/users を叩く（実 Keycloak が後段に居る）。
// テストは**判断の側**（実在しない写像先を保存しないこと）を固定したいので、名簿は差し替える。
//
// 🔴 **`Available` を切り替えられることが要件である。** 「居ない」と「引けなかった」で
// 応答が分かれる（400 / 502）ことは、この 2 状態を作り分けられないと固定できない。
public sealed class StubPlatformUserDirectory : IPlatformUserDirectory
{
    // 既定の名簿。テストは必要に応じて差し替える。
    public HashSet<string> Usernames { get; } = new(StringComparer.Ordinal) { "alice", "bob" };

    // false にすると「名簿を引けなかった」を再現する。
    public bool Available { get; set; } = true;

    // 何回引かれたか。**写像表を送らない要求が名簿を引かない**ことを固定するために使う。
    public int CallCount { get; private set; }

    // [[IADR-0401]] 決定 3 (#1255): 最後に照会された名前。口が「列挙」から「照会」へ変わったので、
    // **送っていない名前を後段へ渡していない**ことをテストから確かめられるようにする。
    public IReadOnlySet<string> LastQuery { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    public Task<PlatformUserDirectorySnapshot> LookupAsync(
        IReadOnlySet<string> usernames, CancellationToken ct)
    {
        CallCount++;
        LastQuery = usernames;
        return Task.FromResult(Available
            // 🔴 **要求した名前との交差を返す**（実装と同じ形）。全件を返すと、
            // 「照会していない名前が実在扱いになる」という実装には無い挙動でテストが緑になる。
            ? PlatformUserDirectorySnapshot.Of(Usernames.Where(usernames.Contains))
            : PlatformUserDirectorySnapshot.Unavailable);
    }
}
