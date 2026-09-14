using System.Text.Json;
using System.Text.RegularExpressions;

namespace Platform.Bff.Foundation.Secrets;

// SC-22, NFR-18, ADR-0095 決定 3, IADR-0433 決定 3, IADR-0453 決定 9 (#1411):
// 画面から投入できる項目の集合（allowlist）。単一情報源は `deploy/bootstrap/sc22-secret-items.json`。
//
// 🔴 **fail-closed。** 読めない・壊れている・`items[]` が空・書けるプロパティと書けないプロパティが
// 交差する・パスにワイルドカードや `..` がある、のいずれでも例外を投げ、BFF は起動しない。
// 「読めなかったから全部許す」も「読めなかったから空にする」も採らない（IADR-0433 決定 3）。
//
// 🔴 **`items[]` だけを読む。** `deferred[]` / `excluded[]` は「なぜ入っていないか」の記録であり、
// 型に持たない —— 持つと「載っているから許す」という誤実装の入口になる。
public sealed record SecretItemDefinition(string Item, string VaultPath, IReadOnlyList<string> Properties);

public sealed class SecretItemCatalogException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed partial class SecretItemCatalog
{
    /// <summary>出力ディレクトリへ同梱する既定のファイル名（csproj の Content リンクと一致させる）。</summary>
    public const string DefaultFileName = "sc22-secret-items.json";

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

                var properties = StringArray(element, "properties", source, required: true);
                if (properties.Count == 0)
                    throw Invalid(source, $"properties が空: {item}");
                var notWritable = StringArray(element, "notWritable", source, required: false);

                // 🔴 書けるプロパティと書けないプロパティが交差したら起動しない。
                // どちらを信じても片方の宣言を黙って捨てることになる。
                var overlap = properties.Intersect(notWritable, StringComparer.Ordinal).ToList();
                if (overlap.Count > 0)
                    throw Invalid(source, $"properties と notWritable が交差している: {item}（{string.Join(", ", overlap)}）");

                items.Add(new SecretItemDefinition(item, vaultPath, properties));
            }

            if (items.Count == 0)
                throw Invalid(source, "items[] が空");

            return new SecretItemCatalog(mount, items);
        }
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
}
