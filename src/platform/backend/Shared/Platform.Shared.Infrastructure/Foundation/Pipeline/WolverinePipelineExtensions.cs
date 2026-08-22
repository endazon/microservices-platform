using System.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Platform.Shared.Infrastructure.Foundation.Pipeline;

// FR-14, FR-15, ADR-0018 / ADR-0027: 宣言的パイプライン構成（pipeline.json）と実装の突合を、
// **MassTransit を要求せずに**行う登録経路。移行チェーンの W2（IADR-0234 決定 3）。
//
// 🔴 **本ファイルは MassTransit 経路（PipelineExtensions）と併存する別 API である。**
// 既存の登録経路・型制約（部分移行の安全弁）は 1 バイトも変えない。安全弁の処分は C3 である
// （IADR-0234 決定 4。U5「型制約の緩和」という単位は起こさない）。
//
// 🔴 **MassTransit 経路との唯一かつ最重要の違いは、入力型を導出できないときの扱いである。**
// 既存経路は `IConsumer<TIn>` から入力型を導出し、**導出できなければ照合ごと飛ばす**
// （`PipelineExtensions.cs` の `inputType is not null &&`）。Wolverine 段には `IConsumer<TIn>` が
// 無いため、その形をそのまま持ち込むと **input 照合が常に素通りする** ——
// 「起動もするしテストも緑だが、宣言と実装がずれていても誰も気づかない」状態になる。
// **本経路は導出できないこと自体を起動失敗にする**（規則 5）。
public static class WolverinePipelineExtensions
{
    // Wolverine がハンドラとして認識するメソッド名。
    //
    // 🔴 **`HandlerChain` の公開定数（Handle / Handles / Consume / Consumes）だけでは足りない。**
    // Wolverine の探索が実際に使うのは `HandlerDiscovery` の内部リストであり、実測すると
    // saga 系（Orchestrate / Start / StartOrHandle / NotFound）を含む 11 個である。
    // 4 個に絞ると **Wolverine が受け付けるハンドラを本経路だけが拒否する**（偽の起動失敗）。
    //
    // 内部フィールドを実行時に読むのは脆いのでここへ複写し、**版更新でずれたら試験が落ちる**
    // ようにしてある（WolverinePipelineExtensionsTests の追随試験）。
    internal static readonly string[] HandlerMethodNames =
    [
        "Handle", "Handles", "Consume", "Consumes",
        "Orchestrate", "Orchestrates", "Start", "Starts",
        "StartOrHandle", "StartsOrHandles", "NotFound",
    ];

    // 段を宣言的構成に従って Wolverine へ登録する。
    //
    // 型制約は `IPipelineStep` だけである —— **`IConsumer` を要求しない**（W2 の要件）。
    // 入力イベント型は `IPipelineStep<TIn>` から取る。
    //
    // 戻り値は解決済みの段宣言である。**受信キューの設定（手順 3）は本経路の射程外**であり、
    // 呼び出し側が `WolverineExtensions.ListenToPlatformQueue` へ渡す。戻り値で返すのは、
    // `queue` 宣言を黙って無視すると「宣言したのに効かない」形になるためである。
    // 宣言が無い（規則 1）ときは null を返す。
    public static PipelineStepOptions? AddPlatformWolverineStep<TStep>(
        this WolverineOptions options, PipelineOptions pipeline, ILogger? logger = null)
        where TStep : class, IPipelineStep
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pipeline);

        var stepName = TStep.StepName;

        // 規則 1: 宣言なし（Steps 空）→ 既定で登録（現行配線と等価。ローカル・テスト互換）。
        if (pipeline.Steps.Count == 0)
        {
            logger?.LogInformation(
                "Pipeline config absent; step {Step} ({Handler}) enabled by default",
                stepName, typeof(TStep).Name);
            options.Discovery.IncludeType<TStep>();
            return null;
        }

        // 規則 2: 宣言があり段が未宣言 → 起動失敗（適用漏れ・名称ずれの検出）。
        var step = pipeline.FindStep(stepName)
            ?? throw new InvalidOperationException(
                $"パイプライン構成に段 '{stepName}' が宣言されていません（pipeline.json の steps を確認）。");

        // 規則 3: consumer 型完全名の照合。
        var handlerType = typeof(TStep).FullName!;
        if (string.IsNullOrEmpty(step.Consumer))
        {
            throw new InvalidOperationException(
                $"段 '{stepName}' の consumer 宣言が空です（pipeline.json の steps を確認）。");
        }
        if (!string.Equals(step.Consumer, handlerType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"段 '{stepName}' の consumer 宣言 '{step.Consumer}' が実装 '{handlerType}' と一致しません。");
        }

        // 規則 4: input 宣言が空 → 起動失敗。
        if (string.IsNullOrEmpty(step.Input))
        {
            throw new InvalidOperationException(
                $"段 '{stepName}' の input 宣言が空です（pipeline.json の steps を確認）。");
        }

        // 規則 5: 🔴 **入力型を導出できないこと自体を起動失敗にする。**
        // MassTransit 経路はここで照合を飛ばすが、それは `IConsumer<TIn>` が事実上必ず在ることに
        // 支えられた挙動である。Wolverine 段にその保証は無く、飛ばせば照合は永久に素通りする。
        var inputType = typeof(TStep).GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineStep<>))
            ?.GetGenericArguments()[0]
            ?? throw new InvalidOperationException(
                $"段 '{stepName}' の実装 '{handlerType}' が IPipelineStep<TIn> を実装していないため"
                + " 入力イベント型を導出できません。宣言との突合が行えないので起動を止めます"
                + "（pipeline.json の input 宣言と実装の対応を固定できません）。");

        // 規則 6: 🔴 **宣言した入力型を実際に受けるハンドラメソッドが在ることまで見る。**
        // IPipelineStep<TIn> は型の自己申告にすぎず、これが無いと規則 7 は
        // 「宣言 対 別の宣言」の照合になり、実装と突き合わせたことにならない。
        if (!HasHandlerMethodFor(typeof(TStep), inputType))
        {
            throw new InvalidOperationException(
                $"段 '{stepName}' の実装 '{handlerType}' に、入力イベント '{inputType.Name}' を受け取る"
                + $" Wolverine ハンドラメソッド（{string.Join(" / ", HandlerMethodNames)}）がありません。");
        }

        // 規則 7: input 宣言と実装の購読イベントの照合。
        if (!string.Equals(step.Input, inputType.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"段 '{stepName}' の input 宣言 '{step.Input}' が実装の購読イベント '{inputType.Name}' と一致しません。");
        }

        // 規則 8: enabled:false → 登録しない（購読・キューを生成しない）。
        if (!step.Enabled)
        {
            logger?.LogWarning(
                "Pipeline step {Step} ({Handler}) is disabled by configuration; "
                + "no subscription or queue will be created", stepName, typeof(TStep).Name);
            return step;
        }

        // 規則 9: 登録する。
        options.Discovery.IncludeType<TStep>();
        logger?.LogInformation(
            "Pipeline step {Step} registered: input={Input} queue={Queue}",
            stepName, step.Input, step.Queue ?? "(default)");
        return step;
    }

    // 名前は「完全一致」か「+ Async」だけを認める。StartsWith で緩く取ると
    // `HandleSomethingElse` のような無関係なメソッドを拾い、規則 6 が素通りする。
    private static bool HasHandlerMethodFor(Type stepType, Type inputType) =>
        stepType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Any(m => IsHandlerMethodName(m.Name)
                      && m.GetParameters().Any(p => p.ParameterType == inputType));

    private static bool IsHandlerMethodName(string name) =>
        HandlerMethodNames.Any(n =>
            string.Equals(name, n, StringComparison.Ordinal)
            || string.Equals(name, n + "Async", StringComparison.Ordinal));
}
