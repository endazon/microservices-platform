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

        // 規則 0: 🔴 **ハンドラ探索の対象アセンブリを、段の実装アセンブリで明示的に固定する。**
        //
        // Wolverine の `ApplicationAssembly` は**プロセス全体で 1 つの静的値**
        // （`WolverineOptions.RememberedApplicationAssembly`）であり、**そのプロセスで最初に起動した
        // ホストが固定する**（Wolverine 6.24.4 / GH-3521。逆コンパイルで実測）。明示しない限り、
        // 2 つ目以降のホストは**自分のアセンブリではなく最初のホストのアセンブリ**を走査する。
        //
        // 本番はサービス 1 つ = プロセス 1 つなので当たらない。**当たるのは統合テストである** ——
        // 1 プロセスで IngestionService と WikiService のホストを同時に立てると、
        // 後発のホストは相手のハンドラを拾い、自分のハンドラを拾わない。そして
        // **その失敗は例外にも再配信にもデッドレターにもならない**:
        //
        //   1. 後発ホストの `DocumentUpdated` チェーンに**相手サービスのコンシューマ**が入る
        //   2. 受信時にそのハンドラの依存が解決できず
        //      `NotSupportedException: Handler type ... does not have a suitable, public constructor`
        //   3. Wolverine は `fail: Exception detected` を 1 行出して**メッセージを ack し捨てる**
        //   4. 同じチェーンに居る**正しいハンドラも道連れで走らない**
        //
        // 症状は「キュー名は正しい・購読は Accepting・30 秒待って 1 通も処理されない」であり、
        // 配送の欠落と見分けがつかない。#1038 → #1059 → #1073 の 3 回、6 ラウンドを費やした
        // （実測は `.ai-context/adr/IADR-0326_wolverine-application-assembly-pinning.md`）。
        //
        // 🔴 **`Discovery.IncludeAssembly` では直らない** —— あれは走査対象を**足す**ので、
        // 相手のアセンブリが走査対象に残り続ける。置き換えるのは `ApplicationAssembly` だけである。
        options.ApplicationAssembly = typeof(TStep).Assembly;

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
            // 🔴 **「IncludeType を呼ばない」だけでは段は無効にならない。**
            // Wolverine の**規約探索**（conventional discovery）は、明示登録とは独立に
            // アセンブリを走査してハンドラを見つける。段の型は普通のハンドラの形をしているので、
            // 何もしなければ **enabled:false のまま購読が生える**。
            //
            // これは FR-14（構成のみで段を外せる）を無言で破る。しかも**走査対象の決まり方が
            // 環境に依存する**ため、「手元では無効・CI では有効」のように*再現しない形*で現れる
            // （#441 E1 で実測: 同じコードが Windows では例外を投げ、Linux の CI では投げなかった。
            // 原因は規約探索が段の型を拾ったこと。`Discovery.IncludeAssembly` で手元でも再現した）。
            //
            // よって**明示的に除外する**。「登録しない」ではなく「登録させない」でなければならない。
            options.Discovery.CustomizeHandlerDiscovery(q => q.Excludes.WithCondition(
                $"パイプライン段 '{stepName}' は構成で無効化されている（pipeline.json enabled:false）",
                t => t == typeof(TStep)));

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
