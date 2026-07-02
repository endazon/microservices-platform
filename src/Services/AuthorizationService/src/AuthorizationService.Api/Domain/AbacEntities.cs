namespace AuthorizationService.Api.Domain;

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
        Dictionary<string, List<string>> userCond, Dictionary<string, List<string>> docCond)
        => new()
        {
            Name = name,
            Action = action,
            UserConditions = userCond,
            DocumentConditions = docCond,
        };

    // FR-09, UC-05: ポリシー本体の更新
    public void Update(string name, string action,
        Dictionary<string, List<string>> userCond, Dictionary<string, List<string>> docCond)
    {
        Name = name;
        Action = action;
        UserConditions = userCond;
        DocumentConditions = docCond;
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

    public static readonly string[] All = [Read, Analyze, Manage];
    public static bool IsValid(string action) => All.Contains(action);
}
