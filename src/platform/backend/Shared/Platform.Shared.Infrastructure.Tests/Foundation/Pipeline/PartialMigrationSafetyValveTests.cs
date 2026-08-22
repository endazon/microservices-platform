using System.Reflection;
using AwesomeAssertions;
using Platform.Shared.Infrastructure.Foundation.Introspection;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Pipeline;

// 🔴 **部分移行（MassTransit 発行 → Wolverine 購読）に対する現存する唯一のコンパイル時安全弁を固定する。**
//
// ADR-0027 は「MT 発行 → Wolverine 購読」の組を禁じる。RabbitMQ の exchange / queue 名の導出規則が
// 両者で異なるため、binding の無い exchange へ publish した結果 **メッセージが黙って捨てられる**
// （publisher confirms は成功を返す）。この退行を止める防壁は 2 つしかない。
//
//   1. `check-event-topology.js` のトランスポート認識判定（#883 / U2 で新設）
//   2. 本テストが固定する型制約
//
// 🔴 **［2026-08-22 訂正 / U4 の独立検証］従前ここには「IConsumer<T> を捨てたコンシューマは
// 登録呼び出しがコンパイルエラーになる」と書いていた。誤りである。** 制約に現れるのは
// **非ジェネリックなマーカー MassTransit.IConsumer** であり、IConsumer と IPipelineStep だけを
// 実装した型は制約を満たして**ビルドが通る**（BUILD_EXIT=0 を実測）。しかもその場合、
// PipelineExtensions の `inputType is not null` により **input 宣言の突合が黙ってスキップされる**。
//
// 🔴 **さらに、コンパイル時の強制力は型制約ではなく本体実装の副作用である。**
// AddPlatformPipelineStep で制約だけを外す変異は 3 通りとも**テストではなくコンパイル**が落ちた。
// 逆に本体を C3 相当（登録呼び出しの撤去）へ変えると **BUILD_EXIT=0 になり、落ちるのは本テストだけ**に
// なった。**本体を書き換えた瞬間、コンパイル時の安全弁は型制約と一緒に失われる。**
// IntrospectionBuilder.AddStep に至っては**現時点で既にコンパイラは何も強制していない**
// （制約を外しても BUILD_EXIT=0。落ちたのは本テスト 1 件だけ）。**その経路の唯一の防壁は本テストである。**
//
// 🔴 **この安全弁は C3 で意図的に外す。** そのとき本テストは落ちる。
// ⚠️ **「U5（型制約の緩和）で外す」は誤りであった**（IADR-0234 決定 4 が IADR-0233 決定 4 を改めた）。
// `AddPlatformPipelineStep` はレシーバごと MassTransit 専用のため**緩和ではなく削除**であり、
// `IntrospectionBuilder.AddStep` は制約を狭めると同時に `input` の導出元を移す**置き換え**である。
// **落ちること自体が目的である** —— 従前この着手順の拘束は申し送りの散文にしか無く、
// 「ヘルパを Wolverine 対応にした瞬間に安全弁が消える」ことを機械は何も知らなかった。
// 本テストを落とさずに型制約を緩めることはできない（IADR-0233 決定 3）。
//
// C3 で外すときは、上記 1 が実際に部分移行を捕まえることを変異試験で確かめたうえで（併せて
// **二重購読が緑のままである負の対照**も取る。上記 1 は見える発行しか覆わない部分的な網であり、
// **型制約と等価ではない** —— IADR-0234 決定 5）、
// 本テストを削除ではなく **「安全弁は検査器側へ移った」ことを示す形へ書き換える**こと。
public class PartialMigrationSafetyValveTests
{
    // 🔴 **MassTransit の型を using で取り込まない。** 本プロジェクトは #455 で新設したもので、
    // ここで `using MassTransit;` を書くと不採用ライブラリの残件が 13 → 14 へ**増える**
    // （check-backend-libraries.js 規則 1 の ratchet が実際に検出した）。安全弁を固定する試験が
    // 撤去対象の依存を増やすのは本末転倒である。完全修飾名で書けば同検査は素通りするが、
    // それは検査の回避であって遵守ではない。**型を取らず、制約の型名で照合する。**
    private const string MassTransitConsumer = "MassTransit.IConsumer";

    private static string[] ConstraintNamesOfSingleGenericParameter(MethodInfo method)
    {
        var parameters = method.GetGenericArguments();
        parameters.Should().HaveCount(1,
            "安全弁の対象は 1 つの型引数（TConsumer）である");
        return [.. parameters[0].GetGenericParameterConstraints().Select(c => c.FullName!)];
    }

    [Fact]
    public void AddPlatformPipelineStep_はIConsumerとIPipelineStepを型制約に持つ()
    {
        var method = typeof(PipelineExtensions)
            .GetMethod(nameof(PipelineExtensions.AddPlatformPipelineStep),
                BindingFlags.Public | BindingFlags.Static);
        // ⚠️ この assert は**到達不能**である（2026-08-22 実測）。上の nameof はコンパイル時束縛のため、
        // メソッドが消えると**本アセンブリが CS0117 で組めず**、ここは評価されない。
        // 登録経路の消滅を守っているのはコンパイラであって本テストではない。public/static を
        // 外す等の将来の経路では意味を持ち得るので残すが、**本テストの功績としては数えない。**
        method.Should().NotBeNull("安全弁を持つ登録経路そのものが消えていない");

        ConstraintNamesOfSingleGenericParameter(method!)
            .Should().Contain([MassTransitConsumer, typeof(IPipelineStep).FullName!]);
    }

    [Fact]
    public void AddStep_はIConsumerとIPipelineStepを型制約に持つ()
    {
        var method = typeof(IntrospectionBuilder)
            .GetMethod(nameof(IntrospectionBuilder.AddStep),
                BindingFlags.Public | BindingFlags.Instance);
        // ⚠️ 上と同じ理由で到達不能である（2026-08-22 実測）。
        method.Should().NotBeNull("安全弁を持つ自己申告経路そのものが消えていない");

        ConstraintNamesOfSingleGenericParameter(method!)
            .Should().Contain([MassTransitConsumer, typeof(IPipelineStep).FullName!]);
    }

    [Fact]
    public void 安全弁は参照型制約も持つ()
    {
        var method = typeof(PipelineExtensions)
            .GetMethod(nameof(PipelineExtensions.AddPlatformPipelineStep),
                BindingFlags.Public | BindingFlags.Static)!;

        method.GetGenericArguments()[0].GenericParameterAttributes
            .Should().HaveFlag(GenericParameterAttributes.ReferenceTypeConstraint);
    }
}
