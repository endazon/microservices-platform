using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents.AddTag;
using DocumentService.Features.Documents.Archive;
using DocumentService.Features.Documents.Create;
using DocumentService.Features.Documents.Delete;
using DocumentService.Features.Documents.GetById;
using DocumentService.Features.Documents.GetVersion;
using DocumentService.Features.Documents.List;
using DocumentService.Features.Documents.ListVersions;
using DocumentService.Features.Documents.Publish;
using DocumentService.Features.Documents.PutBody;
using DocumentService.Features.Documents.Update;
using DocumentService.Features.Documents.UpdateMetadata;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents;

// FR-06, UC-03: 文書 CRUD・バージョン管理・メタデータ管理エンドポイントの合成点。
//
// ADR-0065 決定 2: 各ユースケースの実体は `Features/Documents/<操作>/` に居る。
// **ここに残すのは、操作をまたいで共有されるもの**だけである —— 3 つの route group
// （＝認可の既定。下の注記が示すとおり、この 3 行そのものが規範である）、属性の検証、
// DTO 変換、イベント発行、共通の問題応答。
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        // 読み取り（一覧・個別・版）は一般利用者の文書閲覧（SC-03）のためロールで塞がない。
        // 読み取りの機密制御は取得段の ABAC（IADR-0012）が担う。
        var g = app.MapGroup("/documents").WithTags("Documents");

        // FR-06, FR-09, UC-03, IADR-0044: 多層防御。BFF 迂回の直接呼び出しでも認可を実効化する
        // （サービスが最終防衛線）。利用者トークンは BFF が伝播する。
        //
        // **［#629］このグループ既定は「閲覧の下限」であり、書き込みの実効境界ではない。**
        // 計画 §SC-05「管理系 3 画面の閲覧ロール」（裁定 Q19）は
        // **閲覧を管理者・運用者へ開き、破壊的操作は管理者限定を維持する**と定めている。
        // したがって**個々の書き込み口へ `AdminOnly` を積む**（AND 合成で実効 admin のみ）。
        // **既定を `AdminOnly` へ置き換えないのは、この行が閲覧の下限を表しているからである**
        // （[[IADR-0128]] 決定 1 が #501 で確立し、#628 が踏襲した形）。
        //
        // 🔴 **この 3 行を操作フォルダへ複写しない。** 同じ認可既定が散ると、1 箇所だけ
        // 書き換わって実効境界が静かに変わる形になる（ADR-0065 決定 2 の適用に際しての判断）。
        var write = app.MapGroup("/documents").WithTags("Documents")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        // ── FR-21, UC-03: 文書本文の直接受け入れ（既存文書への投入） ──
        //
        // 🔴 **この群にはロールを積まない。** FR-21 要求文は「本文の書き込み権限は ABAC の**動的束縛**
        // （`doc.owner ∈ { ${current_user} }`）で表現し、**ロールによる判定を追加しない**」と定めており、
        // ADR-0036 D-07 も同じことを述べている。**上の `write` 群（admin / operator）へ入れてはならない**
        // —— 入れると受け入れ基準 ⑤「一般利用者が自分の文書の本文を投入できる」が満たせなくなる。
        // 認証だけは要る（主体が決まらないと動的束縛が評価できない）。
        var bodyIntake = app.MapGroup("/documents").WithTags("Documents").RequireAuthorization();

        // ── FR-18, SC-03, ADR-0063 決定 3, IADR-0364 (#1187): AI タグ提案の承認の反映先 ──
        //
        // 🔴 **この群にもロールを積まない。** 認可は「①所有者の動的束縛 **または** ②管理者ロール」の
        // 選言であり（決定 3）、group にロールを積むと①の側（自分の文書の提案を承認する一般利用者）が
        // 死ぬ。判定は口の中で行い、拒否は 404 に倒す（`PutBody` と同じ）。**`write` 群へ入れてはならない。**
        var tagReflection = app.MapGroup("/documents").WithTags("Documents").RequireAuthorization();

        ListDocumentsEndpoint.Map(g);
        GetDocumentEndpoint.Map(g);
        CreateDocumentEndpoint.Map(write);
        UpdateDocumentEndpoint.Map(write);
        UpdateDocumentMetadataEndpoint.Map(write);
        PublishDocumentEndpoint.Map(write);
        ArchiveDocumentEndpoint.Map(write);
        PutDocumentBodyEndpoint.Map(bodyIntake);
        AddDocumentTagEndpoint.Map(tagReflection);
        ListDocumentVersionsEndpoint.Map(g);
        GetDocumentVersionEndpoint.Map(g);
        DeleteDocumentEndpoint.Map(write);

        return app;
    }

    // FR-05, UC-03, SC-05, IADR-0047: 機密区分（必須属性）検証。NG のとき 400 の IResult を、
    // 妥当なとき null を返す（呼び出し側は `is { } error` で早期リターンする）。
    internal static IResult? ConfidentialityProblemOrNull(Dictionary<string, string>? attributes)
    {
        var (ok, error) = DocumentAttributes.ValidateConfidentiality(attributes);
        return ok
            ? null
            : Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [DocumentAttributes.ConfidentialityKey] = [error!]
            });
    }

    // FR-06, FR-19, ADR-0058 決定 2, [[IADR-0278]]: doc_scope の不変性検証。
    // **値域検証（DocScopeProblemOrNull）とは別の検査である** —— あちらは「知らない値か」を、
    // こちらは「作成時に確定した値から動いたか」を見る。**既存文書が要るため取得の後に呼ぶ。**
    internal static IResult? DocScopeChangedProblemOrNull(
        Dictionary<string, string>? incoming, IReadOnlyDictionary<string, string> current)
    {
        var (ok, error) = DocumentAttributes.ValidateDocScopeUnchanged(incoming, current);
        return ok
            ? null
            : Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [DocumentAttributes.DocScopeKey] = [error!]
            });
    }

    // FR-19, ADR-0054, [[IADR-0270]] 決定 2: doc_scope（文書スコープ）の値域検証。
    // 🔴 欠落は拒否しない（既存文書は遡及付与しない方針 — ADR-0054 §結果）。未知値のみ 400。
    internal static IResult? DocScopeProblemOrNull(Dictionary<string, string>? attributes)
    {
        var (ok, error) = DocumentAttributes.ValidateDocScope(attributes);
        return ok
            ? null
            : Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [DocumentAttributes.DocScopeKey] = [error!]
            });
    }

    // FR-09, SC-09, #635: **外へ出す形は表示名である**（正本は識別子。[[IADR-0153]] 決定 2）。
    // 契約（`DocumentDto.Tags`）は `List<string>` のままで、**下流も画面も変わらない**。
    internal static DocumentDto ToDto(Document d, IReadOnlyDictionary<Guid, string> names) => new()
    {
        Id = d.Id,
        Title = d.Title,
        Status = d.Status,
        MarkdownUri = d.MarkdownUri,
        Version = d.Version,
        Attributes = d.Attributes,
        Tags = TagResolver.ToNames(d.Tags, names),
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
        // SC-03, ADR-0070 決定 3 / [[IADR-0388]] 決定 2 (#1254): 本文なしの文書を
        // 文書詳細が区別できるようにする（表示は SC-02 と同じ「本文なし（原本を参照）」）。
        HasBody = d.HasBody,
    };

    // **過去版も現在の表示名で出る**——改名は表示上の変更である（[[IADR-0153]] 決定 4）。
    //
    // 🔴 **本文の参照は載せない**（#1011 / [[IADR-0290]]）。`DocumentVersion.MarkdownUri` は
    // スナップショット時点の**文書の**本文 URI であり、オブジェクトキーが文書 ID で固定のため
    // **常に現行版の本文を指す**。載せると 200 の応答に「その版の本文らしい URI」が入り、
    // 呼び出し側が過去版の本文だと読み違えても区別できない。契約（`DocumentVersionDto`）から
    // 落としてあるので、ここで写す先も無い。**戻さないこと。**
    internal static DocumentVersionDto ToVersionDto(DocumentVersion v, IReadOnlyDictionary<Guid, string> names) => new()
    {
        DocumentId = v.DocumentId,
        Version = v.Version,
        Title = v.Title,
        Status = v.Status,
        Attributes = v.Attributes,
        Tags = TagResolver.ToNames(v.Tags, names),
        ChangeNote = v.ChangeNote,
        CreatedAt = v.CreatedAt,
    };

    // FR-06, UC-03 / ADR-0027（E3b）: DocumentUpdated の発行（Wolverine。IDocumentUpdatedPublisher 経由）。
    // **イベントも表示名を運ぶ。** 射影（Qdrant / Wiki.js）は人が読む面であり、
    // 検索の hot path に辞書引きを増やさない（[[IADR-0153]] 決定 1・2）。
    //
    // **［#635］`internal` にしてある。** 改名の再発行（`Tags/Rename`）が同じ形を要るためで、
    // **識別子 → 表示名の変換点を 2 つに割らない**ことがここでの目的である（同 決定 2）。
    // （旧 `ToEvent` の後継。イベントの構築はアダプタ側にある —— 可視発行を 1 点に保つため。）
    // **［#1184］共有先（`shared_with`）の解決もここで行う**（ADR-0061 決定 5 / [[IADR-0394]] 決定 3）。
    // 🔴 **呼び出し側に解決させない。** 「共有先を載せる経路」と「載せない経路」に割れると、
    // 索引の中の判定軸が経路ごとに違うものになり、**どちらが正しいかを誰も言えなくなる**
    // （識別子 → 表示名の変換点を 1 つに保っているのと同じ理由）。
    internal static async Task PublishUpdatedAsync(IDocumentUpdatedPublisher bus,
        DocumentDbContext db, Document d,
        IReadOnlyDictionary<Guid, string> names, CancellationToken ct = default)
    {
        var sharedWith = await db.DocumentShares
            .Where(s => s.DocumentId == d.Id)
            .Select(s => s.SubjectId)
            .ToListAsync(ct);

        await bus.PublishUpdatedAsync(d.Id, d.Title, d.Status, d.MarkdownUri,
            d.Attributes, TagResolver.ToNames(d.Tags, names), d.UpdatedAt,
            d.ContentFingerprint, d.HasBody, d.OriginalPath, d.DataSourceName, sharedWith, ct);
    }

    // FR-19, ADR-0061 決定 1・2 / [[IADR-0394]] 決定 4 (#1184): **発行の門。**
    //
    // 🔴 **個人資料は「3 トグルのうち 1 つでも ON」のときだけ索引の生産側へ流す。**
    // 3 つとも OFF の資料は**イベントそのものを出さない** —— OFF を「索引に存在しない」ことで
    // 構造的に守る性質（[[IADR-0270]] 決定 5 が守っていたもの）をそのまま残すためである。
    //
    // **判定は `DocumentExposure.IsIndexable` ただ 1 つ**であり、索引の生産側
    // （`IngestionService.DocumentUpdatedConsumer`）が呼ぶのと**同じ関数**である。
    // 組織文書は常に true（露出キーを持たない）なので、既存経路の挙動は変わらない。
    internal static Task PublishUpdatedIfIndexableAsync(IDocumentUpdatedPublisher bus,
        DocumentDbContext db, Document d,
        IReadOnlyDictionary<Guid, string> names, CancellationToken ct = default)
        => DocumentExposure.IsIndexable(d.Attributes)
            ? PublishUpdatedAsync(bus, db, d, names, ct)
            : Task.CompletedTask;

    // FR-21 受け入れ基準 ⑥: 本文が上限を超えたときの応答。**413 であって 400 ではない**
    // （計画が status を名指ししている）。本文へ上限を書き、切り詰めた成功と取り違えられないようにする。
    internal static IResult BodyTooLargeProblem() => Results.Problem(
        title: "本文が上限を超えています。",
        detail: $"本文の上限は {DocumentBodyIntake.MaxBytes} バイト（UTF-8）です。"
              + "上限を超える本文は切り詰めずに拒否します。",
        statusCode: StatusCodes.Status413PayloadTooLarge);

    // SC-05, #635: 辞書に無いタグ名を 400 にする（「既定タグ辞書に整合」。手入力は自動登録しない）。
    internal static IResult UnknownTagsProblem(List<string> unknown) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["tags"] = [$"辞書に無いタグです: {string.Join(" / ", unknown)}。SC-09 の辞書へ先に登録してください。"],
        });
}
