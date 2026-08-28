namespace NotificationService.Features.Notifications;

// FR-22: 主体（JWT の sub）からメール宛先を解決する port。
//
// ★ **解決元は決まっていない。** Keycloak の利用者属性か独自の連絡先かは、機能仕様書の
// 未決事項 2 が明示的に未決としている。**ここで決めない** —— port だけ置き、
// 既定実装は「解決できない」を返す。
public interface IEmailAddressResolver
{
    Task<string?> ResolveAsync(string subject, CancellationToken ct = default);
}

// 既定実装。**null を返す＝宛先不明**であり、送出は `failed` として記録される（沈黙しない）。
public sealed class UnresolvedEmailAddressResolver : IEmailAddressResolver
{
    public Task<string?> ResolveAsync(string subject, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
