namespace AuthorizationService.Api.Foundation.Domain;

// FR-05, FR-09, ADR-0004: ABAC エンティティ群

// FR-09, UC-05: 属性辞書エントリ（管理者が定義する取りうる値）
public class AttributeDefinition
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Key { get; private set; } = string.Empty;        // "confidentiality", "department" …
    public string Label { get; private set; } = string.Empty;
    public List<string> AllowedValues { get; private set; } = [];  // ["public","internal","confidential","restricted"]
    public bool Required { get; private set; }
    public string Scope { get; private set; } = AttributeScope.Document; // document | user
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private AttributeDefinition() { }

    public static AttributeDefinition Create(string key, string label, List<string> allowedValues,
        bool required, string scope)
        => new()
        {
            Key = key,
            Label = label,
            AllowedValues = allowedValues,
            Required = required,
            Scope = scope,
        };

    // FR-09, UC-05: 属性辞書の更新。Key / Scope は同一性・一意制約の基礎のため不変とする。
    public void Update(string label, List<string> allowedValues, bool required)
    {
        Label = label;
        AllowedValues = allowedValues;
        Required = required;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

// FR-09, UC-05: ABAC ポリシー（利用者属性 × 文書属性 → 許可アクション）
public class AbacPolicy
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = string.Empty;
    public string Action { get; private set; } = PolicyAction.Read; // read | analyze | manage
    // 利用者属性条件: {"clearance": ["confidential","restricted"], "roles": ["admin"]}
    public Dictionary<string, List<string>> UserConditions { get; private set; } = [];
    // 文書属性条件: {"confidentiality": ["public","internal"], "lifecycle": ["active"]}
    public Dictionary<string, List<string>> DocumentConditions { get; private set; } = [];
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private AbacPolicy() { }

    public static AbacPolicy Create(string name, string action,
        Dictionary<string, List<string>>? userCond, Dictionary<string, List<string>>? docCond)
        => new()
        {
            Name = name,
            Action = action,
            // FR-09: 条件は null を保存しない。省略時は「条件なし」（空辞書）として扱い、
            // 評価エンジン（AbacEvaluator）が null を foreach して落ちるのを防ぐ。
            UserConditions = userCond ?? [],
            DocumentConditions = docCond ?? [],
        };

    // FR-09, UC-05: ポリシー本体の更新
    public void Update(string name, string action,
        Dictionary<string, List<string>>? userCond, Dictionary<string, List<string>>? docCond)
    {
        Name = name;
        Action = action;
        // FR-09: Create と同じく null を保存しない（「条件なし」= 空辞書）。
        UserConditions = userCond ?? [];
        DocumentConditions = docCond ?? [];
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    // FR-09, UC-05: 有効／無効の切替（削除せず一時停止できるようにする）
    public void SetActive(bool isActive)
    {
        IsActive = isActive;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public static class AttributeScope
{
    public const string Document = "document";
    public const string User = "user";

    public static readonly string[] All = [Document, User];
    public static bool IsValid(string scope) => All.Contains(scope);
}

public static class PolicyAction
{
    public const string Read = "read";
    public const string Analyze = "analyze";
    public const string Manage = "manage";
    // FR-05, FR-21, ADR-0036 D-01/D-07, IADR-0253 決定 5（2026-08-23 改定 / #989）:
    // 計画の write 規則（doc.owner ∈ { ${current_user} }）を同じポリシーモデルで表現するための値。
    // 計画 07_abac-attribute-model §ポリシー評価モデル は 2026-08-22 追記で値域へ write を加えており
    // （planning#466）、実装が追随する。**値域の拡張そのものは何も許可しない** ——
    // deny-by-default により、write ポリシーが 1 件も無い間は write スコープは全件遮断のままである。
    public const string Write = "write";

    public static readonly string[] All = [Read, Analyze, Manage, Write];
    public static bool IsValid(string action) => All.Contains(action);
}
