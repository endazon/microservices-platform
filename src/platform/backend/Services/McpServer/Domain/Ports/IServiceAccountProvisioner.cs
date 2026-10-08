namespace McpServer.Domain.Ports;

// FR-16, FR-09, UC-09, SC-12, 計画 ADR-0123 決定 1・2・3, ADR-0062 決定 3, ADR-0088 決定 1, [[IADR-0516]] (#1786):
// **SC-12 を IdP への入口にする書き込み口。** 無人（サービスアカウント）の MCP クライアントについて、
// IdP（Keycloak）に機密クライアントとサービスアカウントを作り、割り当てた ABAC 属性を
// `service-account-<client>` の利用者属性として書く。
//
// ■ 🔴 **この口は検証しない。** 部分集合の判定と個人資料の割当禁止は呼び出し元（`McpClientEndpoints.RejectUnassignableAsync`）が
//   **この口を呼ぶ前に**掛ける（ADR-0123 決定 3）。ここに 2 つ目の判定を置くと、片方だけが緩む。
// ■ 🔴 **書いたものは取り消せる形で返す**（<see cref="IdpWrite"/>）。登録簿への書き込みが後で失敗したら、
//   呼び出し元は <see cref="UndoAsync"/> で IdP を書く前の状態へ戻す（補償。IADR-0516 決定 4）。
// ■ 🔴 **入口が作っていないクライアントへは書かない**（<see cref="IdpWriteKind.AlreadyExists"/>）。登録では IdP に同じ clientId が
//   あれば、差し替えでは入口の印（`managed-by`）が無ければ、何も書かない。入口を通らずに作られたクライアント
//   （プラットフォーム自身の機密クライアントを含む）へ属性を書くと、検証の掛からない主体ができ、その主体の本来の属性を
//   上書きもする（ADR-0123 決定 2 の禁止。PR #1816 の監査 🔴-1）。
// ■ 🔴 **要求の取り消しを IdP への書き込みへ伝えない。** 書きかけで止めると孤児が残る。期限は口の HttpClient の Timeout が持ち、
//   時間切れは `Failed`（502）へ写す。補償も取り消しに依らず最後まで走る。
public interface IServiceAccountProvisioner
{
    /// <summary>
    /// 登録: 機密クライアント（サービスアカウントつき）を作り、属性を書く。
    /// 同じ clientId のクライアントが IdP に既に在れば何も書かずに <see cref="IdpWriteKind.AlreadyExists"/> を返す。
    /// 途中で失敗したら、作ったクライアントを消してから <see cref="IdpProvisioningException"/> を投げる。
    /// </summary>
    Task<IdpWrite> CreateAsync(
        string clientId, string displayName, IReadOnlyDictionary<string, string> attributes, CancellationToken ct);

    /// <summary>
    /// 属性の差し替え: サービスアカウントの利用者属性を、与えた集合で置き換える。
    /// IdP にクライアントが無ければ（本入口ができる前の登録簿の行）作ってから書く（<see cref="IdpWriteKind.Created"/>）。
    /// そのとき <paramref name="enabled"/> が false（登録簿で無効化された行）なら、**無効のまま**作る。
    /// IdP にあっても入口の印が無ければ、何も書かずに <see cref="IdpWriteKind.AlreadyExists"/> を返す。
    /// </summary>
    Task<IdpWrite> ReplaceAttributesAsync(
        string clientId, string displayName, IReadOnlyDictionary<string, string> attributes, bool enabled, CancellationToken ct);

    /// <summary>
    /// ［2026-10-09 / #1829］無効化・再有効化の写し（IADR-0516 決定 4a。多層の防御）: 入口の印つきのクライアントの
    /// <c>enabled</c> を <paramref name="enabled"/> にする（同じ値なら書かない）→ <see cref="IdpWriteKind.EnabledChanged"/>。
    /// IdP に同じ clientId のクライアントが無ければ <see cref="IdpWriteKind.Absent"/>、入口の印が無ければ
    /// <see cref="IdpWriteKind.AlreadyExists"/> を返し、**どちらも何も書かない**（プラットフォームのクライアントを変えない）。
    /// 書いた後に読み戻せなければ、前の値へ戻してから <see cref="IdpProvisioningException"/> を投げる。
    /// </summary>
    Task<IdpWrite> SetEnabledAsync(string clientId, bool enabled, CancellationToken ct);

    /// <summary>
    /// 補償: <paramref name="write"/> を書く前の状態へ戻す（作ったなら消し、書き換えたなら元の属性を書き戻す）。
    /// 🔴 書き換えの取り消しは、IdP の現在値がまだ <see cref="IdpWrite.WrittenAttributes"/> と同じときだけ書き戻す
    /// （並行した差し替えの新しい値を古い値で潰さない）。<c>enabled</c> の取り消しも同じ規則（現在値が書いた値のときだけ戻す）。
    /// </summary>
    Task UndoAsync(IdpWrite write, CancellationToken ct);
}

public enum IdpWriteKind
{
    /// <summary>クライアントを作り、属性を書いた。取り消しはクライアントの削除。</summary>
    Created,

    /// <summary>既存のサービスアカウントの属性を書き換えた。取り消しは <see cref="IdpWrite.PreviousAttributes"/> の書き戻し。</summary>
    Updated,

    /// <summary>入口が作っていないクライアントが IdP に既に在った（同じ clientId・または印が無い）。**何も書いていない。**</summary>
    AlreadyExists,

    /// <summary>［#1829］入口の印つきのクライアントの <c>enabled</c> を書いた。取り消しは <see cref="IdpWrite.PreviousEnabled"/> の書き戻し。</summary>
    EnabledChanged,

    /// <summary>［#1829］IdP に同じ clientId のクライアントが無い（入口ができる前の行）。**何も書いていない。**取り消すものも無い。</summary>
    Absent,
}

// 書いた結果。補償に要る情報（IdP 側の識別子・書く前の属性）だけを持つ。
public sealed record IdpWrite(
    IdpWriteKind Kind,
    string ClientId,
    string? ClientInternalId = null,
    string? ServiceAccountUserId = null,
    IReadOnlyDictionary<string, string>? PreviousAttributes = null,
    IReadOnlyDictionary<string, string>? WrittenAttributes = null,
    bool? PreviousEnabled = null,
    bool? WrittenEnabled = null)
{
    public static IdpWrite AlreadyExisting(string clientId) => new(IdpWriteKind.AlreadyExists, clientId);

    public static IdpWrite Missing(string clientId) => new(IdpWriteKind.Absent, clientId);
}

public enum IdpProvisioningFailure
{
    /// <summary>書き込み口が構成されていない（配備が IdP への入口を宣言していない）。**何も書いていない。**</summary>
    Unavailable,

    /// <summary>IdP への書き込み・照合が失敗した。作りかけのものは口の側で消してある（消せなければその旨をログに残す）。</summary>
    Failed,
}

// 🔴 **「書けなかった」を「拒否した」と混ぜない。** 前者は 503 / 502、後者（検証の外れ）は 400 である。
public sealed class IdpProvisioningException(IdpProvisioningFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public IdpProvisioningFailure Failure { get; } = failure;
}
