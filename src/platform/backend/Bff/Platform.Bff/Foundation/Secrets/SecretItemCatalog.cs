using System.Text.Json;
using System.Text.RegularExpressions;

namespace Platform.Bff.Foundation.Secrets;

// SC-22, NFR-18, ADR-0095 決定 3, IADR-0433 決定 3, IADR-0453 決定 9 (#1411), IADR-0456 決定 1・4 (#1477):
// 画面から投入できる項目の集合（allowlist）。単一情報源は `deploy/bootstrap/sc22-secret-items.json`。
//
// 🔴 **fail-closed。** 読めない・壊れている・`items[]` が空・書けるプロパティと書けないプロパティが
// 交差する・パスにワイルドカードや `..` がある・プロパティの種別や `sensitive` が不正・ExternalSecret の宣言が
// 無い／不正／重複する、のいずれでも例外を投げ、BFF は起動しない。
// 「読めなかったから全部許す」も「読めなかったから空にする」も採らない（IADR-0433 決定 3）。
//
// 🔴 **`items[]` だけを読む。** `deferred[]` / `excluded[]` は「なぜ入っていないか」の記録であり、
// 型に持たない —— 持つと「載っているから許す」という誤実装の入口になる。

/// <summary>
/// プロパティの値の作り方（IADR-0456 決定 1）。
/// </summary>
public enum SecretPropertyKind
{
    /// <summary>画面が送った値をそのまま書く（既定）。</summary>
    Value,

    /// <summary>画面は平文のパスワードを送り、BFF が MD5（小文字 hex 32 桁）へ変換して書く。平文は保存しない。</summary>
    Md5FromPassword,

    /// <summary>画面は値を送らず、BFF が RSA 1024 bit の PKCS#1 PEM を生成して書く。鍵はどこにも返さない。</summary>
    GenerateRsaPkcs1,
}

/// <summary>書けるプロパティ 1 つ。`Sensitive` が false でも書き込み専用である（読み出す口は無い）。</summary>
public sealed record SecretPropertyDefinition(string Name, SecretPropertyKind Kind, bool Sensitive)
{
    /// <summary>allowlist・契約での種別の綴り。</summary>
    public string KindName => SecretItemCatalog.KindNameOf(Kind);
}

/// <summary>書き込み後に `force-sync` の注釈を付ける ExternalSecret（IADR-0456 決定 4）。</summary>
public sealed record ExternalSecretReference(string Name, string Namespace);

public sealed record SecretItemDefinition(
    string Item,
    string VaultPath,
    IReadOnlyList<SecretPropertyDefinition> PropertyDefinitions,
    ExternalSecretReference ExternalSecret)
{
    /// <summary>書けるプロパティ名（allowlist の並び順）。</summary>
    public IReadOnlyList<string> Properties { get; } = [.. PropertyDefinitions.Select(p => p.Name)];

    /// <summary>書けるプロパティを名前で引く。無ければ null（`notWritable` を含む）。</summary>
    public SecretPropertyDefinition? FindProperty(string? name) =>
        name is null ? null : PropertyDefinitions.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}

public sealed class SecretItemCatalogException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed partial class SecretItemCatalog
{
    /// <summary>出力ディレクトリへ同梱する既定のファイル名（csproj の Content リンクと一致させる）。</summary>
    public const string DefaultFileName = "sc22-secret-items.json";

    public const string KindValue = "value";
    public const string KindMd5FromPassword = "md5-from-password";
    public const string KindGenerateRsaPkcs1 = "generate-rsa-pkcs1";

    private static readonly HashSet<string> PropertyObjectKeys = new(StringComparer.Ordinal) { "name", "kind", "sensitive" };

    private readonly Dictionary<string, SecretItemDefinition> _byItem;

    private SecretItemCatalog(string vaultMount, IReadOnlyList<SecretItemDefinition> items)
    {
        VaultMount = vaultMount;
        Items = items;
        _byItem = items.ToDictionary(i => i.Item, StringComparer.Ordinal);
    }

    /// <summary>KV v2 のマウント名（例 `secret`）。</summary>
    public string VaultMount { get; }

    /// <summary>画面が扱う項目（ファイルの並び順）。</summary>
    public IReadOnlyList<SecretItemDefinition> Items { get; }

    /// <summary>項目名で引く。allowlist に無ければ null（呼び出し側は 400 を返す）。</summary>
    public SecretItemDefinition? Find(string item) =>
        _byItem.TryGetValue(item, out var definition) ? definition : null;

    internal static string KindNameOf(SecretPropertyKind kind) => kind switch
    {
        SecretPropertyKind.Md5FromPassword => KindMd5FromPassword,
        SecretPropertyKind.GenerateRsaPkcs1 => KindGenerateRsaPkcs1,
        _ => KindValue,
    };

    public static SecretItemCatalog Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            throw new SecretItemCatalogException(
                $"SC-22 の項目一覧（allowlist）を読めない: {path}。BFF は起動しない（fail-closed）。", ex);
        }

        return Parse(json, path);
    }

    internal static SecretItemCatalog Parse(string json, string source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw Invalid(source, "JSON として解釈できない", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Invalid(source, "最上位がオブジェクトではない");

            var mount = RequiredString(root, "vaultMount", source);
            if (!SegmentPattern().IsMatch(mount))
                throw Invalid(source, "vaultMount の書式が不正");

            if (!root.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
                throw Invalid(source, "items[] が無い");

            var items = new List<SecretItemDefinition>();
            var seenItems = new HashSet<string>(StringComparer.Ordinal);
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);
            var seenExternalSecrets = new HashSet<ExternalSecretReference>();
            foreach (var element in itemsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    throw Invalid(source, "items[] の要素がオブジェクトではない");

                var item = RequiredString(element, "item", source);
                if (!SegmentPattern().IsMatch(item))
                    throw Invalid(source, $"item の書式が不正: {item}");
                if (!seenItems.Add(item))
                    throw Invalid(source, $"item が重複している: {item}");

                // 🔴 ワイルドカード（`*` / `+`）・`..`・先頭末尾の `/` を許さない。
                // policy は完全一致パスで書く（IADR-0433 決定 1）ので、ここで緩めると字面の突合が崩れる。
                var vaultPath = RequiredString(element, "vaultPath", source);
                if (!VaultPathPattern().IsMatch(vaultPath))
                    throw Invalid(source, $"vaultPath の書式が不正（ワイルドカード・.. は不可）: {item}");
                if (!seenPaths.Add(vaultPath))
                    throw Invalid(source, $"vaultPath が重複している: {item}");

                var properties = PropertyArray(element, item, source);
                if (properties.Count == 0)
                    throw Invalid(source, $"properties が空: {item}");
                var notWritable = StringArray(element, "notWritable", source, required: false);

                // 🔴 書けるプロパティと書けないプロパティが交差したら起動しない。
                // どちらを信じても片方の宣言を黙って捨てることになる。
                var overlap = properties.Select(p => p.Name).Intersect(notWritable, StringComparer.Ordinal).ToList();
                if (overlap.Count > 0)
                    throw Invalid(source, $"properties と notWritable が交差している: {item}（{string.Join(", ", overlap)}）");

                // IADR-0456 決定 4: 書き込み後に同期を依頼する ExternalSecret。🔴 **無い項目を許さない** ——
                // 許すと「書けたのに反映されない」項目が黙って混ざり、RBAC の resourceNames との突合も崩れる。
                var externalSecret = ExternalSecretOf(element, item, source);
                if (!seenExternalSecrets.Add(externalSecret))
                    throw Invalid(source, $"externalSecret が重複している: {item}");

                items.Add(new SecretItemDefinition(item, vaultPath, properties, externalSecret));
            }

            if (items.Count == 0)
                throw Invalid(source, "items[] が空");

            return new SecretItemCatalog(mount, items);
        }
    }

    // IADR-0456 決定 1: `properties[]` の要素は文字列（種別 `value`・秘密）か、`{ name, kind?, sensitive? }` のオブジェクト。
    // 🔴 未知のキー（綴り違いを含む）・未知の種別・真偽値でない `sensitive` は起動しない。
    // 🔴 `sensitive: false` は `value` にだけ許す —— パスワードと生成した鍵を「秘密でない」と宣言させない。
    private static List<SecretPropertyDefinition> PropertyArray(JsonElement element, string item, string source)
    {
        if (!element.TryGetProperty("properties", out var value))
            throw Invalid(source, "properties が無い");
        if (value.ValueKind != JsonValueKind.Array)
            throw Invalid(source, "properties が配列ではない");

        var list = new List<SecretPropertyDefinition>();
        foreach (var entry in value.EnumerateArray())
        {
            string? name;
            var kind = SecretPropertyKind.Value;
            var sensitive = true;
            switch (entry.ValueKind)
            {
                case JsonValueKind.String:
                    name = entry.GetString();
                    break;
                case JsonValueKind.Object:
                    foreach (var key in entry.EnumerateObject())
                    {
                        if (!PropertyObjectKeys.Contains(key.Name))
                            throw Invalid(source, $"properties の要素に未知のキーがある: {item}（{key.Name}）");
                    }

                    name = entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    if (entry.TryGetProperty("kind", out var k))
                        kind = k.ValueKind == JsonValueKind.String ? ParseKind(k.GetString(), item, source)
                            : throw Invalid(source, $"properties の kind が文字列ではない: {item}");
                    if (entry.TryGetProperty("sensitive", out var s))
                        sensitive = s.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => throw Invalid(source, $"properties の sensitive が真偽値ではない: {item}"),
                        };
                    break;
                default:
                    throw Invalid(source, $"properties の要素が文字列でもオブジェクトでもない: {item}");
            }

            if (string.IsNullOrWhiteSpace(name) || !PropertyPattern().IsMatch(name))
                throw Invalid(source, "properties の要素の書式が不正");
            if (list.Exists(p => string.Equals(p.Name, name, StringComparison.Ordinal)))
                throw Invalid(source, $"properties の要素が重複している: {name}");
            if (!sensitive && kind != SecretPropertyKind.Value)
                throw Invalid(source, $"sensitive: false は kind が value のプロパティにだけ付けられる: {item}（{name}）");

            list.Add(new SecretPropertyDefinition(name, kind, sensitive));
        }

        return list;
    }

    private static SecretPropertyKind ParseKind(string? kind, string item, string source) => kind switch
    {
        KindValue => SecretPropertyKind.Value,
        KindMd5FromPassword => SecretPropertyKind.Md5FromPassword,
        KindGenerateRsaPkcs1 => SecretPropertyKind.GenerateRsaPkcs1,
        _ => throw Invalid(source, $"properties の kind が未知: {item}（{kind}）"),
    };

    private static ExternalSecretReference ExternalSecretOf(JsonElement element, string item, string source)
    {
        if (!element.TryGetProperty("externalSecret", out var value) || value.ValueKind != JsonValueKind.Object)
            throw Invalid(source, $"externalSecret が無い: {item}");

        var name = RequiredString(value, "name", source);
        var ns = RequiredString(value, "namespace", source);
        if (name.Length > 253 || !KubernetesNamePattern().IsMatch(name))
            throw Invalid(source, $"externalSecret.name の書式が不正: {item}");
        if (ns.Length > 63 || !KubernetesNamespacePattern().IsMatch(ns))
            throw Invalid(source, $"externalSecret.namespace の書式が不正: {item}");
        return new ExternalSecretReference(name, ns);
    }

    private static string RequiredString(JsonElement element, string name, string source)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid(source, $"{name} が無い");
        }

        return value.GetString()!;
    }

    private static List<string> StringArray(JsonElement element, string name, string source, bool required)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            if (required) throw Invalid(source, $"{name} が無い");
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
            throw Invalid(source, $"{name} が配列ではない");

        var list = new List<string>();
        foreach (var entry in value.EnumerateArray())
        {
            var text = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
            if (string.IsNullOrWhiteSpace(text) || !PropertyPattern().IsMatch(text))
                throw Invalid(source, $"{name} の要素の書式が不正");
            if (list.Contains(text, StringComparer.Ordinal))
                throw Invalid(source, $"{name} の要素が重複している: {text}");
            list.Add(text);
        }

        return list;
    }

    private static SecretItemCatalogException Invalid(string source, string reason, Exception? inner = null) =>
        new($"SC-22 の項目一覧（allowlist）が不正: {source}: {reason}。BFF は起動しない（fail-closed）。", inner);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*(/[a-z0-9][a-z0-9-]*)*$")]
    private static partial Regex VaultPathPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex PropertyPattern();

    // k8s のオブジェクト名（DNS-1123 subdomain）と名前空間名（DNS-1123 label）。
    [GeneratedRegex(@"^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*$")]
    private static partial Regex KubernetesNamePattern();

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
    private static partial Regex KubernetesNamespacePattern();
}
