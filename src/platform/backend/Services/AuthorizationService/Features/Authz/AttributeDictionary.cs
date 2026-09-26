using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;
using AuthorizationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Features.Authz;

// FR-05, FR-09, UC-05, SC-09, SC-17, 計画 ADR-0116 決定 3, ADR-0115 決定 1, [[IADR-0476]] (#1609):
// **属性辞書を読む唯一の入口**。`department` の許可値を realm の部門グループから導いて当てはめる。
//
// ■ 🔴 **属性辞書を読む経路はすべてここを通す**（一覧・個別取得・登録・更新・ポリシー検証〔保存・dry-run〕・
//   利用者属性の差し替え〔SC-17〕・文書属性の検証）。1 経路でも保存済みの値を直に読むと、画面に出る値と検証が使う値が
//   食い違う（画面は realm の値を出すのに、保存は seed の `finance` を通す、等）。
//
// ■ realm を読めたら、導いた値が保存済みと違うときだけ**保存し直す**（最後に確かめた値として残す）。
//   読めないときはその保存済みの値を使い、**消さない**（`DepartmentDictionaryValues` の注記）。
//   保存し直しは読み取りの副作用だが、値は realm から一意に決まるので、並行した保存は同じ値を書くだけである。
//
// ■ realm の読み取りは要求ごとに行う（キャッシュしない）。管理画面と検証の頻度は低く、部門グループの変更が
//   次の要求で辞書に現れることを優先する。
public sealed class AttributeDictionary(IIdentityAdminClient identity, ILogger<AttributeDictionary> logger)
{
    /// <summary>
    /// realm の部門グループのコードを読む。根（`/department`）が無い・読み取りが例外なら <see cref="DepartmentDomainReading.Unknown"/>。
    /// 🔴 **取り消し（要求の中断）だけは上げる**（不明へ畳まない）。
    /// </summary>
    public async Task<DepartmentDomainReading> ReadDepartmentDomainAsync(CancellationToken ct)
    {
        try
        {
            var root = await identity.FindGroupByPathAsync(
                DepartmentAttributeReconciliation.DepartmentGroupRoot.TrimEnd('/'), ct);
            if (root is null)
            {
                logger.LogWarning(
                    "属性辞書: realm に /department グループが無い。部門の許可値を導けないため不明として扱い、保存済みの値を消さない。");
                return DepartmentDomainReading.Unknown;
            }

            var children = await identity.ListSubGroupsAsync(root.Id, ct);
            return DepartmentDomainReading.Of(children
                .Select(g => DepartmentAttributeReconciliation.CodeOf(g.Path))
                .Where(code => code is not null)
                .Select(code => code!));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "属性辞書: realm の部門グループを読めなかった。部門の許可値は不明として扱い、保存済みの値を消さない。");
            return DepartmentDomainReading.Unknown;
        }
    }

    /// <summary>
    /// 属性辞書を読み、`department` の許可値を当てはめて返す（追跡つき）。realm を読めて値が変わっていれば保存する。
    /// </summary>
    public async Task<Snapshot> LoadAsync(AuthorizationDbContext db, CancellationToken ct)
    {
        var definitions = await db.AttributeDefinitions.ToListAsync(ct);
        var reading = definitions.Any(d => DepartmentDictionaryValues.IsDerived(d.Key))
            ? await ReadDepartmentDomainAsync(ct)
            : DepartmentDomainReading.Unknown;

        if (reading.Known && Apply(definitions, reading))
            await db.SaveChangesAsync(ct);

        return new Snapshot(definitions, reading);
    }

    /// <summary>
    /// 導くキーの定義の許可値を realm のコードへ置き換える。1 つでも変わったら true。
    /// 🔴 realm を読めないときは何もしない（呼び出し元が Known を確かめる）。
    /// </summary>
    internal static bool Apply(IEnumerable<AttributeDefinition> definitions, DepartmentDomainReading reading)
    {
        var changed = false;
        foreach (var definition in definitions.Where(d => DepartmentDictionaryValues.IsDerived(d.Key)))
        {
            if (definition.AllowedValues.SequenceEqual(reading.Codes, StringComparer.Ordinal)) continue;
            definition.Update(definition.Label, [.. reading.Codes], definition.Required);
            changed = true;
        }
        return changed;
    }

    /// <summary>応答の形（エンティティの項目 ＋ 許可値の出所）。</summary>
    public static AttributeDefinitionView ToView(AttributeDefinition definition, DepartmentDomainReading reading)
        => new(definition.Id, definition.Key, definition.Label, definition.AllowedValues, definition.Required,
            definition.Scope, definition.CreatedAt, definition.UpdatedAt,
            DepartmentDictionaryValues.SourceOf(definition.Key, reading));

    /// <summary>当てはめ済みの属性辞書と、その時の realm の読み取り結果。</summary>
    public sealed record Snapshot(List<AttributeDefinition> Definitions, DepartmentDomainReading Reading)
    {
        public List<AttributeDefinitionView> Views() => [.. Definitions.Select(d => ToView(d, Reading))];
    }
}

// FR-09, SC-09, [[IADR-0476]] (#1609): 属性辞書の応答（BFF ↔ SPA 契約 `AttributeDefinitionDto` と JSON 互換）。
// `AllowedValuesSource` は手で持つキーでは null、`department` では `realm`（導いた）／`realm-unavailable`（不明・最後に確かめた値）。
public sealed record AttributeDefinitionView(
    Guid Id,
    string Key,
    string Label,
    List<string> AllowedValues,
    bool Required,
    string Scope,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? AllowedValuesSource);
