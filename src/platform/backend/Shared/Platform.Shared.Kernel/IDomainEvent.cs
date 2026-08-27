namespace Platform.Shared.Kernel;

/// <summary>
/// ドメインイベントのマーカー。集約（<see cref="AggregateRoot{TId}"/>）が状態変化の事実を
/// 表すために発生させる。
/// </summary>
/// <remarks>
/// NFR / IADR-0280 決定 6: 全サービスの Domain が共有する基底であるため、Domain の唯一の
/// 許容参照先である本プロジェクトに置く（計画 12_backend-application-stack の構成図
/// 「SharedKernel = Result / Error・共通基底」）。
///
/// **意図的にメンバを持たないマーカーである。** 発生時刻・イベント ID 等のメタデータは、
/// 発行の仕組み（Wolverine のエンベロープ）が運ぶ情報と重複するため、必要になった実例が
/// 出るまで足さない（過剰な基盤化をしない —— 同決定）。サービス間へ運ぶイベント契約は
/// 従来どおり各ユニットの Contracts プロジェクトに置き、本インターフェースを契約へ載せない。
/// </remarks>
public interface IDomainEvent;
