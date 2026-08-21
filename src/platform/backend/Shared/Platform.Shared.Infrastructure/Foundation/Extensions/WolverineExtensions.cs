using JasperFx.CodeGeneration.Model;
using Wolverine;
using Wolverine.RabbitMQ;

namespace Platform.Shared.Infrastructure.Foundation.Extensions;

// ADR-0027 移行チェックリスト 手順 3・4・5 を全サービス共通で満たすための Wolverine 設定拡張。
//
// 🔴 **本ファイルは 3 手順の唯一の実装箇所である**（submodule の別プロジェクトは射程外）。
// 手順 6 が「3〜5 を共通ヘルパへ封じ込め、個別サービスでの逸脱を静的検査で禁止する」と定めるため、
// scripts/check-backend-libraries.js の規則 5 が (a) 本ファイル以外での使用を fail させ、
// (b) **本ファイルから消えたことも** fail させる。封じ込めは「他所で書けない」だけでは半分で、
// 「ここに在り続ける」が要る（IADR-0233 決定 2）。
//
// 手順 3〜5 はいずれも「怠っても起動し、ビルドもテストも通り、実行時に静かに壊れる」種類の設定である。
// 手順 3 を怠ると pub/sub が competing consumer へ退行して片方だけが受信し、手順 4 を怠ると発行が
// プロセス内へ閉じ、手順 5 を怠ると internal 実装型に依存するハンドラが最初の受信時に落ちる。
//
// MassTransit 経路（MassTransitExtensions / PipelineExtensions）とは併存する別 API であり、
// 本ファイルは既存の登録経路を一切変更しない（部分移行に対する型制約の安全弁は U5 まで残す）。
public static class WolverineExtensions
{
    // 手順 3: リスニングキュー名にサービス名を前置する。
    //
    // 前置の目的は fan-out の保存である。同一イベントを 2 サービスが購読するとき（正本の
    // pipeline.json では DocumentUpdated を ingestion-service と wiki-service が購読する）、
    // キュー名が同じになると RabbitMQ は competing consumer となり **丁度 1 つだけが受信する**。
    // サービス名を前置すればキューは必ず分かれ、両方が受信する。
    //
    // 区切りは "." である（"-" ではない）。サービス名自体が kebab-case（wiki-service）であり、
    // "-" で繋ぐと "wiki-service-DocumentUpdated" のどこまでがサービス名か読めない。
    // 隣接プロジェクト ai-stock-trading も同じ問題に対して "." を採っている（同 IADR-0129 決定 1）。
    //
    // 🔴 **exchange 名には前置しない。** 前置すると発行側の exchange と食い違い「誰にも届かない」形になる。
    // ブローカ側の一括前置 API と規約ルーティングはいずれも ADR-0027 手順 3 が名指しで禁じており、
    // その 2 つの API 名は本ファイルに書かない —— check-backend-libraries.js 規則 4 が
    // **.cs 中のコメントも含めて**名前の出現自体を fail させる（「コメントに書いてから外す」経路を塞ぐ設計）。
    // 名前と経緯は docs/tech/tech-requirements.md にある。
    public static string PlatformQueueName(string serviceName, string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        return $"{serviceName}.{queueName}";
    }

    // 手順 3 の適用点。個別サービスは ListenToRabbitQueue を直接呼ばず、必ずこれを通す
    // （直接呼び出しは規則 5(a) が fail させる）。
    //
    // ⚠️ この行のシンボル名はコメントにも本文にも現れるが、規則 5(b) は**コメントを除いたコードに対して
    // 呼び出し構文で**照合するため、実装が消えれば検出される。**この二重の出現は意図的に残している** ——
    // 実リポジトリの状態そのものが「コメント越しのすり抜け」に対する恒常的な回帰試験になる。
    public static RabbitMqListenerConfiguration ListenToPlatformQueue(
        this WolverineOptions options, string serviceName, string queueName)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ListenToRabbitQueue(PlatformQueueName(serviceName, queueName));
    }

    // 手順 4・5: 全サービス共通のメッセージング既定値。各サービスの UseWolverine 内で 1 回呼ぶ。
    public static WolverineOptions UsePlatformMessagingDefaults(this WolverineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 手順 4: 発行元プロセスに同じ型のハンドラがあると、Wolverine の既定の規約ルーティングが
        // 発行をプロセス内へ閉じてしまい、外部購読者へ出て行かなくなる。規約を切って明示配線に寄せる。
        options.Policies.DisableConventionalLocalRouting();

        // 手順 5: Wolverine は既定で ServiceLocationPolicy.NotAllowed であり（実測）、生成コードから
        // 直接構築できない internal 実装型に依存するハンドラは **最初のメッセージ受信時に** 落ちる。
        // 実行時コンパイル（IADR-0217）と組み合わさるため起動時には現れない。サービスロケーションを
        // 明示的に許可して、この「受信するまで分からない失敗」を消す。
        options.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;

        return options;
    }
}
