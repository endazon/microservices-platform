namespace DataSourceService.Api.Foundation.Domain;

// FR-01, UC-04: データソースエンティティ
public class DataSource
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = string.Empty;
    public string SourceType { get; private set; } = string.Empty; // filesystem|wiki|saas|db
    public string ConnectionUri { get; private set; } = string.Empty;
    public string Status { get; private set; } = DataSourceStatus.Active;
    public DateTimeOffset? LastSyncedAt { get; private set; }
    public Dictionary<string, string> Config { get; private set; } = [];

    // FR-01, FR-05, ADR-0004: このデータソース由来の原本へ既定で付与する ABAC 文書属性。
    // 取り込み時に RawDocumentFetched.Attributes へ写像され、下流の fail-closed 検索（IADR-0012）で
    // 文書が機密区分（confidentiality）欠落により除外されるのを防ぐ。
    public Dictionary<string, string> DefaultAttributes { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    // FR-01, FR-02, UC-04, SC-06（Q14 / #537）: 同期健全性。**エンティティに持って永続化する**。
    // 従前はインメモリの計数（singleton の SyncFailureTracker）だけを持っていたが、**読み口へは
    // 流用できない** —— プロセスローカルな計数は再起動で消え、読み取りがどのインスタンスへ当たるかで
    // 値が割れる。計画が「静かに壊れる機能に気づく手段」と位置づけた表示の土台としては成立しない。
    // 移行にあたり当該クラスは削除した（同じ数を 2 箇所に持たない）。根拠は IADR-0148。
    public int ConsecutiveFailureCount { get; private set; }

    // 直近の同期エラー。メッセージは接続文字列由来の秘密を含み得るため、記録側でマスクしてから渡す。
    public string? LastSyncError { get; private set; }
    public DateTimeOffset? LastSyncErrorAt { get; private set; }

    private DataSource() { }

    // FR-05, ADR-0004: 機密区分の許可値は AuthorizationService の属性辞書に準拠（public|internal|confidential|restricted）。
    // データソース登録時に既定機密区分が未指定の場合のフェイルセーフ既定値。public（過剰公開）でも
    // restricted（過剰制限）でもなく、社内文書の基準となる internal を採る。
    public const string ConfidentialityKey = "confidentiality";
    public const string DefaultConfidentiality = "internal";

    public static DataSource Create(string name, string sourceType, string connectionUri,
        Dictionary<string, string>? config = null,
        Dictionary<string, string>? defaultAttributes = null)
    {
        return new()
        {
            Name = name,
            SourceType = sourceType,
            ConnectionUri = connectionUri,
            Config = config ?? [],
            // FR-01, FR-05: 原本には機密区分を必ず付与する。未指定・空はフェイルセーフ既定値で補う。
            DefaultAttributes = WithConfidentialityFailsafe(defaultAttributes),
        };
    }

    // FR-01, FR-05, IADR-0019: 原本発行時に必ず通るフェイルセーフ。`DefaultAttributes` に機密区分が
    // 欠落・空でも internal を補完した属性辞書を返す。sync が本アクセサ経由で属性を組み立てることで、
    // 本対応マージ前から登録済みで confidentiality を持たない既存データソースでも、fail-closed 除外
    // （IADR-0012）を再発させない最終防衛線となる。呼び出しごとに新しい辞書を返す（防御的コピー）。
    public Dictionary<string, string> GetEffectiveAttributes() => WithConfidentialityFailsafe(DefaultAttributes);

    // FR-05: 機密区分（confidentiality）の欠落・空を既定値で補う。`Create`（登録時）と
    // `GetEffectiveAttributes`（発行時）で挙動が乖離しないよう補完ロジックを一元化する。
    private static Dictionary<string, string> WithConfidentialityFailsafe(IReadOnlyDictionary<string, string>? attributes)
    {
        var result = attributes is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(attributes);
        if (!result.TryGetValue(ConfidentialityKey, out var conf) || string.IsNullOrWhiteSpace(conf))
            result[ConfidentialityKey] = DefaultConfidentiality;
        return result;
    }

    public void RecordSync()
    {
        LastSyncedAt = DateTimeOffset.UtcNow;
    }

    // FR-01, UC-04, SC-06（Q16 / #534）: 全置換（PUT）。**Id / CreatedAt / LastSyncedAt / 同期健全性は
    // 変えない** —— Q16 の目的は「削除→再登録が ID と履歴を切る」ことの解消であり、更新で履歴を
    // 巻き戻せてはならない。DefaultAttributes は登録時と同じフェイルセーフを必ず通す（下記 Patch も同様）。
    public void Update(string name, string sourceType, string connectionUri,
        Dictionary<string, string>? config = null,
        Dictionary<string, string>? defaultAttributes = null)
    {
        Name = name;
        SourceType = sourceType;
        ConnectionUri = connectionUri;
        // IADR-0148 決定 6: 応答のマスク値（***）を書き戻しても本物の秘密を壊さない。
        Config = SecretConfigMask.PreserveMasked(config ?? [], Config);
        DefaultAttributes = WithConfidentialityFailsafe(defaultAttributes);
    }

    // FR-01, UC-04, SC-06（Q16 / #534）: 部分更新（PATCH）。**null の項目は現状維持**である。
    // 接続先だけ・認証情報だけの差し替えを、他項目を読んで書き戻す往復なしに行えるようにする
    // （往復させると応答のマスク済みの値〔***〕を書き戻して秘密を破壊する）。
    public void Patch(string? name = null, string? sourceType = null, string? connectionUri = null,
        Dictionary<string, string>? config = null,
        Dictionary<string, string>? defaultAttributes = null)
    {
        if (name is not null) Name = name;
        if (sourceType is not null) SourceType = sourceType;
        if (connectionUri is not null) ConnectionUri = connectionUri;
        // IADR-0148 決定 6: 同上。PATCH は「読んで一部だけ直して送り返す」経路そのものなので、
        // ここが無いと最も普通の使い方が資格情報を破壊する。
        if (config is not null) Config = SecretConfigMask.PreserveMasked(config, Config);
        // FR-05: 属性を差し替えるときも機密区分のフェイルセーフを通す。空にできると fail-closed 検索
        // （IADR-0012）から文書が落ちる。省略時（null）は現状維持なので補完も走らせない。
        if (defaultAttributes is not null) DefaultAttributes = WithConfidentialityFailsafe(defaultAttributes);
    }

    // FR-01, UC-04, SC-06（Q14 / #537）: 同期失敗を記録し、更新後の連続失敗回数を返す。
    // errorMessage は呼び出し側でマスク済みの文字列を渡す（本エンティティはマスクの責務を持たない）。
    public int RecordSyncFailure(string? errorMessage, DateTimeOffset occurredAt)
    {
        ConsecutiveFailureCount++;
        LastSyncError = errorMessage;
        LastSyncErrorAt = occurredAt;
        return ConsecutiveFailureCount;
    }

    // FR-01, UC-04, SC-06（Q14 / #537）: 同期の完全成功で健全性を初期状態へ戻す。
    // **直近エラーも消す** —— 残すと「正常なのに ⚠ の材料が残っている」状態になり、画面の判断が割れる。
    public void ClearSyncFailures()
    {
        ConsecutiveFailureCount = 0;
        LastSyncError = null;
        LastSyncErrorAt = null;
    }

    public void Disable() => Status = DataSourceStatus.Disabled;
}

public static class DataSourceStatus
{
    public const string Active = "active";
    public const string Disabled = "disabled";
}
