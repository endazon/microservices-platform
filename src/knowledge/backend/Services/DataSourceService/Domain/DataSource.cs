namespace DataSourceService.Domain;

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

    // FR-05, UC-04, ADR-0036, #516: システム投入経路（人が居ない取り込み）での所有者・所管部門。
    // 計画確定（planning#344 の裁定・2026-08-15。正は計画
    // `06_technical/09_datasource-connectors.md` §システム投入経路での `owner` / `department`）。
    //
    // **予約値は「既定」ではなく「解決できなかったことの記録」である。**
    // 恒久的に積み上がるなら、それは *コネクタが更新者・部門を運んでいない* という報告であって
    // 正常な状態ではない。**件数を観測し、環流債務の測定値として読む**（IADR-0199 決定 3）。
    //
    // **どちらも deny 側に倒れる** —— ポリシーが allowedDepartments に unassigned を列挙しない限り、
    // その文書は属性ベースの分岐で到達できない（「未設定は公開しない」と整合）。
    public const string OwnerKey = "owner";
    public const string DepartmentKey = "department";
    public const string LifecycleKey = "lifecycle";

    // 解決できないときに入れる予約値。**欠落させない**（欠落と予約値は区別できる必要がある ——
    // 欠落は「計画が必須と定めた属性が無い」、予約値は「必須は満たしたが解決できなかった」）。
    public const string UnresolvedOwner = "system";
    public const string UnresolvedDepartment = "unassigned";

    // FR-05, UC-04, #516: `lifecycle` の終端（裁定 2026-08-15・裁定依頼 planning#361。案 C ＋ 終端 active）。
    //
    // **これは「予約値」ではなく「既定値」である** —— `owner` / `department` の `system` / `unassigned` は
    // 「解決できなかったことの記録」だが、`active` は**そう決めた値**である。件数を債務として数えない。
    //
    // **`active` にしても無制限に公開にはならない。** `read` は属性の連言であり、
    // `confidentiality`（未指定は internal）と `department`（未解決は deny 側の unassigned）が同時にかかる。
    // **可視性の統制を lifecycle 単独に負わせていない**（計画の理由書き）。
    //
    // **ソース単位で下書き扱いにしたい場合はデータソースの既定属性で `draft` を指定する。**
    // 終端の `active` は指定が無いときだけ効く。
    public const string DefaultLifecycle = "active";

    // FR-05, ADR-0054 決定 5 (#1009): システム投入経路（人が居ない取り込み）で作られる文書の
    // `doc_scope` の既定。**取り込み経路が個人資料を作ることはない**ため、既定が終端である
    // （`owner` / `department` と違い「解決できないときの予約値」を持たない）。
    public const string DocScopeKey = "doc_scope";
    public const string DefaultDocScope = "organization";

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
            // FR-01, FR-05, #516: 原本には計画が必須と定める属性を必ず付与する。
            // 未指定・空はフェイルセーフ既定値・予約値で補う。
            DefaultAttributes = WithRequiredAttributeFailsafe(defaultAttributes),
        };
    }

    // FR-01, FR-05, IADR-0019: 原本発行時に必ず通るフェイルセーフ。`DefaultAttributes` に必須属性が
    // 欠落・空でも既定値・予約値を補完した属性辞書を返す。sync が本アクセサ経由で属性を組み立てることで、
    // 本対応マージ前から登録済みで confidentiality を持たない既存データソースでも、fail-closed 除外
    // （IADR-0012）を再発させない最終防衛線となる。呼び出しごとに新しい辞書を返す（防御的コピー）。
    public Dictionary<string, string> GetEffectiveAttributes() => GetEffectiveAttributes(null);

    // FR-05, UC-04, #752 段 1: **アイテム単位の上書きを受け取る解決口。**
    //
    // 従前は本メソッドの引数なし版しか無く、取り込み経路は**ソース単位で 1 回だけ**属性を計算して
    // 全アイテムへ同じ辞書を配っていた。計画 09_datasource-connectors が `owner` の既定を
    // 「**ソース側の更新者**・作成者を利用者識別子へ解決して入れる」と定める以上、
    // **アイテムごとに違う値が入りうる**のだから、ソース単位の計算では原理的に載せられない。
    //
    // 🔴 **優先順位は 3 段で、上ほど強い。**
    //   1. `DefaultAttributes` の明示指定 —— **上書きしない**（既存規約。`Create_WithExplicitOwner_PreservesValue`）
    //   2. `perItem`（アイテム単位。コネクタが運んできた更新者など）
    //   3. 予約値（`system` / `unassigned`）—— 計画「解決できないとき」
    //
    // `perItem` が null・空・空白値なら**何も起きない**（1 バイトも挙動が変わらない）。
    // 本段ではどのコネクタも値を載せないため、実際に変わるのは段 2 以降である。
    public Dictionary<string, string> GetEffectiveAttributes(IReadOnlyDictionary<string, string>? perItem)
    {
        if (perItem is null || perItem.Count == 0)
            return WithRequiredAttributeFailsafe(DefaultAttributes);

        var merged = DefaultAttributes is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(DefaultAttributes);

        foreach (var (key, value) in perItem)
        {
            // 空白の上書きは**何も足さない**（「運んでこなかった」と同じ扱い）。
            if (string.IsNullOrWhiteSpace(value)) continue;

            // 🔴 **空白だけでなく「予約値が入っている」ときも上書きする。**
            //
            // `Create` / `Update` / `Patch` は登録・更新の時点で失敗安全を通すため、
            // `DefaultAttributes` には**既に予約値が焼き込まれている**。したがって
            // 「空白のときだけ埋める」規則では、アイテム単位の値は**永久に載らない**
            // （実測して発見した。#752 段 1）。
            //
            // 予約値を上書きしてよい根拠は計画にある —— 「**`system` / `unassigned` は
            // 『解決できなかった』ことの記録であり、既定ではない**」。**記録は、解決できたら
            // 置き換わるべきものである。**
            //
            // **利用者が明示的に `system` を指定した場合も上書きされる。** 予約値は
            // 「主体を指す値」ではなく「未解決の印」なので、それでよい。
            if (!IsUnresolved(merged, key)) continue;
            merged[key] = value;
        }

        return WithRequiredAttributeFailsafe(merged);
    }

    // FR-05, UC-04, ADR-0036, #516: 計画が**必須**と定める文書属性の欠落・空を補う。`Create`（登録時）・
    // `Update` / `Patch`（更新時）・`GetEffectiveAttributes`（発行時）で挙動が乖離しないよう一元化する。
    //
    // **明示指定は上書きしない。** 補うのは「欠落・空白のみ」の場合に限る（従前の confidentiality と同じ規約）。
    //
    // **`lifecycle` は 2026-08-15 の追補裁定（planning#361）で終端 `active` が確定した。**
    // 従前は「未裁定のため補完しない」としていたが、**裁定が下りたので補完する**。
    private static Dictionary<string, string> WithRequiredAttributeFailsafe(IReadOnlyDictionary<string, string>? attributes)
    {
        var result = attributes is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(attributes);

        FillIfBlank(result, ConfidentialityKey, DefaultConfidentiality);

        // `department` はデータソース既定属性から補い、それも無ければ予約値を入れる。
        // **終端まで書く** —— 既定部門が必ず設定される保証はどこにも無く、終端が無いと
        // `owner` にだけ受け皿があって `department` は黙って欠落する非対称が残る（計画の理由書き）。
        //
        // **供給源は存在するが写像が未実装である。** `SourceItem.Path` はフォルダを運んでおり、計画は
        // 「ソースのメタ（所在・部門・フォルダ・更新者等）を ABAC 基本属性へマッピングする」
        // （09_datasource-connectors L51）・ファイルサーバーは「フォルダ単位の既定属性を継承」（同 L34）と
        // 定めている。欠けているのは**フォルダ → 部門コードの写像規則**である。追跡は #754。
        //
        // ［2026-08-28 追記 / #754］**従前ここには「加えて SC-06 の登録フォームに入力欄が無い」と
        // 書いていたが、これは誤りになった。** 登録側は #767、更新側は #754 で入力欄が着地し、
        // **供給源②（データソースの既定属性）は画面から開くようになった。**
        //
        // 🔴 **残る①（フォルダ → 部門の写像）は「未実装」ではなく「実装してはならない」である。**
        // 裁定（2026-08-16。planning#372）は「**部門コードの値域が定まるまで `department` の写像は
        // 行わない**」と明記しており、値域（既存の部門マスタの所在）は**組織側で未確定**である。
        // **実装側でフォルダ名から推定規則を決めない** —— 誤った部門は ADR-0034 の判定を狂わせる。
        FillIfBlank(result, DepartmentKey, UnresolvedDepartment);

        // `owner` はソース側の更新者を解決して入れるのが正である。
        //
        // ［2026-08-21 追記 / #752 段 1］**器は作った。** 従前ここには「コネクタの契約
        // `SourceItem(Path, ModifiedAt, Size)` は更新者を運ばない」「器そのものが無い」と
        // 書いていたが、`SourceItem.UpdatedBy` と `GetEffectiveAttributes(perItem)` を足したので
        // **経路は通っている**。**まだ倒れる理由は変わった** —— 器が無いからではなく、
        // **どのコネクタも値を載せていない**からである。追跡は引き続き #752。
        //
        // ［2026-08-28 追記 / #752］**「残る 1 本は識別子の名前空間が未裁定」は誤りになった。**
        // 解決順は **① Keycloak ユーザー検索 → ② データソース単位の写像表 → 予約値 `system`** と
        // **確定済み**である（裁定 2026-08-16。planning#371）。未裁定なのではなく、
        // **①が未配備・②の写像表が組織側で未確定**なため、解決器が 1 つも無い。
        //
        // 🔴 **したがって `db` コネクタに更新者列を足してはならない**（4 実装のうち
        // `filesystem` / `wiki` / `saas` の 3 本は構造上取れず、`db` だけが載せられる）。
        // 載せると生の列値が解決を経ずに `owner` になる（`DataSourceSyncService.PerItemAttributes`）。
        // 計画は「**別名前空間の識別子をそのまま `owner` へ入れてはならない**」「誤った写像は
        // **偽の所有者**を作り、**裁量制御が意図しない相手に開く**」「安全側は『解決しない』」と
        // 定める。**値の搭載は解決器の配備とセットでのみ行える。**
        //
        // **ただし「常に」予約値になるわけではない** —— `DefaultAttributes` に明示指定があれば
        // 上書きしない（テスト `Create_WithExplicitOwner_PreservesValue`）。API 経由なら現在も設定できる。
        FillIfBlank(result, OwnerKey, UnresolvedOwner);

        // `lifecycle` はデータソース既定属性で指定でき、指定が無ければ `active` へ倒す
        // （裁定 planning#361。`department` と同じ 3 段の形だが、**ソース側から解決する対応物が無い**
        // ためファイルの状態からは決まらず、1 段目が無い形になる）。
        FillIfBlank(result, LifecycleKey, DefaultLifecycle);

        // `doc_scope` は取り込み経路では常に `organization`（ADR-0054 決定 5・
        // 09_datasource-connectors の既定表「解決の余地が無い」）。**1 段の形である** ——
        // ソース側から解決する対応物が無く、予約値も持たない。
        //
        // 🔴 **これが入らないと、`doc_scope` をポリシーの文書条件に名指した瞬間に、
        // 取り込み済みの文書が一斉に不可視化する**（`AbacEvaluator` は属性キーの欠落を
        // 不一致に倒すため）。名指しは個人資料を組織文書の経路から締め出すための本筋の
        // 手段であり、その前提としてここが要る。
        FillIfBlank(result, DocScopeKey, DefaultDocScope);

        return result;
    }

    // 欠落・空白のみのときだけ埋める。既に値があれば触らない。
    // #752 段 1: そのキーが「まだ解決できていない」状態かを判定する。
    // 空白（未設定）に加え、**そのキーの予約値**も未解決とみなす（上の理由書きを参照）。
    private static bool IsUnresolved(IReadOnlyDictionary<string, string> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return true;

        return key switch
        {
            OwnerKey => value == UnresolvedOwner,
            DepartmentKey => value == UnresolvedDepartment,
            // 予約値の概念を持たないキー（confidentiality / lifecycle 等）は、
            // 値がある時点で解決済みとみなす。**明示指定を上書きしない。**
            _ => false,
        };
    }

    private static void FillIfBlank(Dictionary<string, string> attributes, string key, string fallback)
    {
        if (!attributes.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            attributes[key] = fallback;
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
        DefaultAttributes = WithRequiredAttributeFailsafe(defaultAttributes);
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
        // FR-05: 属性を差し替えるときも必須属性のフェイルセーフを通す。空にできると fail-closed 検索
        // （IADR-0012）から文書が落ちる。省略時（null）は現状維持なので補完も走らせない。
        if (defaultAttributes is not null) DefaultAttributes = WithRequiredAttributeFailsafe(defaultAttributes);
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
