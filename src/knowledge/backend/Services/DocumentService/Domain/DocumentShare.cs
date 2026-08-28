namespace DocumentService.Domain;

// FR-19, FR-20, ADR-0036 D-06/D-11, ADR-0046 D-06 部品 3, IADR-0253 決定 4（段 4）:
// 文書の共有先（文書 ID × 被共有主体）。
//
// **属性辞書（Document.Attributes）には載せない** —— 値が単一文字列であり集合を持てないうえ、
// 共有は属性とライフサイクルが違う（付与・取り消し・監査が要る。ADR-0036 は再共有不可・
// 取り消し可を定めている）。属性辞書へ多値を持ち込むと、属性を読むすべての面
// （検索・グラフ・Wiki ゲートウェイ・BFF）の契約が変わるため、専用の記録として持つ。
//
// 共有の単位は**個人とグループの両方**（ADR-0036 D-06）。共有先には書き込み権限を与えない
// （計画 write 規則は owner のみ）。共有相手の退職時の自動解除（D-11）は人事連携
// プロビジョニング側の作業であり、本エンティティは削除可能な形（取り消し＝行削除）で備える。
public class DocumentShare
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid DocumentId { get; private set; }
    public string SubjectType { get; private set; } = ShareSubjectType.User;   // user | group
    public string SubjectId { get; private set; } = string.Empty;              // 利用者/グループ識別子
    // 監査用: 共有を付与した主体（所有者）。ADR-0036 D-06「所有者が明示的に共有した相手」の記録。
    public string GrantedBy { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private DocumentShare() { }

    public static DocumentShare Create(Guid documentId, string subjectType, string subjectId,
        string grantedBy)
        => new()
        {
            DocumentId = documentId,
            SubjectType = subjectType,
            SubjectId = subjectId,
            GrantedBy = grantedBy,
        };
}

// FR-20, ADR-0036 D-06: 共有先の種別。個人とグループの 2 値（集合を勝手に増やさない）。
public static class ShareSubjectType
{
    public const string User = "user";
    public const string Group = "group";

    public static readonly string[] All = [User, Group];
    public static bool IsValid(string subjectType) => All.Contains(subjectType);
}
