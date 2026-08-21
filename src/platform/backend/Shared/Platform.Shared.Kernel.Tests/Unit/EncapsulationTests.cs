using System.Reflection;
using AwesomeAssertions;

namespace Platform.Shared.Kernel.Tests.Unit;

/// <summary>
/// NFR / #455 / 計画 ADR-0041 決定 2 の機械的な担保。
///
/// 「Domain / Application / Api / Infrastructure は SharedKernel が公開する型のみを参照し、
/// 外部ライブラリの型・名前空間を直接参照してはならない」を成立させるには、**SharedKernel の
/// 公開面に外部型が 1 つも出ていない**ことが前提になる。出ていれば、呼び出し側は SharedKernel を
/// 経由したまま外部 API に触れてしまい、封じ込めが形骸化する。
///
/// csproj の静的検査（scripts/check-backend-libraries.js）は「どのパッケージを参照してよいか」
/// までしか見ない。**公開面に漏れているかは見ない。** その面をここで固定する。
/// </summary>
public class EncapsulationTests
{
    private const string ForbiddenNamespace = "CSharpFunctionalExtensions";

    private static readonly Assembly KernelAssembly = typeof(Result).Assembly;

    /// <summary>
    /// 公開型のメソッド・プロパティ・フィールド・コンストラクタの**シグネチャ**に、
    /// 外部ライブラリの型が現れないこと。private フィールドは対象外（内部実装は自由）。
    /// </summary>
    [Fact]
    public void 公開面に外部ライブラリの型が現れない()
    {
        var leaks = new List<string>();

        foreach (var type in KernelAssembly.GetExportedTypes())
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Collect(leaks, $"{type.Name}.{m.Name} の戻り値", m.ReturnType);
                foreach (var p in m.GetParameters())
                {
                    Collect(leaks, $"{type.Name}.{m.Name} の引数 {p.Name}", p.ParameterType);
                }
            }

            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Collect(leaks, $"{type.Name}.{p.Name} プロパティ", p.PropertyType);
            }

            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Collect(leaks, $"{type.Name}.{f.Name} フィールド", f.FieldType);
            }

            foreach (var c in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var p in c.GetParameters())
                {
                    Collect(leaks, $"{type.Name} のコンストラクタ引数 {p.Name}", p.ParameterType);
                }
            }
        }

        leaks.Should().BeEmpty(
            "計画 ADR-0041 決定 2: 外部ライブラリは SharedKernel の内部実装としてのみ使い、公開面へ出さない");
    }

    /// <summary>
    /// 内部実装としては実際に使っていること。使っていなければ、この検査は
    /// 「そもそも依存が無い」ことを見ているだけになり、封じ込めを試験していない
    /// （0 件走査で緑を返さない。IADR-0130 と同じ考え方）。
    /// </summary>
    [Fact]
    public void 内部実装では外部ライブラリを実際に使っている()
    {
        KernelAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Should().Contain(ForbiddenNamespace,
                "封じ込めの試験が成立するには、内部で実際に依存していることが要る");
    }

    private static void Collect(List<string> leaks, string where, Type type)
    {
        foreach (var t in Flatten(type))
        {
            if (t.Namespace is not null && t.Namespace.StartsWith(ForbiddenNamespace, StringComparison.Ordinal))
            {
                leaks.Add($"{where}: {t.FullName}");
            }
        }
    }

    /// <summary>ジェネリック引数・配列要素・by-ref を展開して、包まれた型も見る。</summary>
    private static IEnumerable<Type> Flatten(Type type)
    {
        if (type.IsByRef || type.IsArray || type.IsPointer)
        {
            var inner = type.GetElementType();
            if (inner is not null)
            {
                foreach (var t in Flatten(inner)) yield return t;
            }

            yield break;
        }

        yield return type;

        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                foreach (var t in Flatten(arg)) yield return t;
            }
        }
    }
}
