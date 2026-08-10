namespace Knowledge.Contracts.Dtos;

// FR-12, UC-06, SC-07, IADR-0154: 変換ジョブが抽出した図 1 つ分（BFF ↔ SPA 契約）。
//
// **人手補正 Phase 1 の対象は Coded == false の図に限る**（05_screens:330「Phase 1 は図のコード化の
// やり直しに限る」）。コード化に成功した図（Coded == true）は Language / Code を持ち、本文には
// コードブロックとして埋め込まれている。縮退した図は ImageUri を持ち、本文には画像参照として
// 埋め込まれている（NormalizationService の埋め込み形は決定的である。IADR-0154 決定 3）。
//
// **画像そのもの（バイト列）はここに載せない。** 一覧の応答が図の枚数だけ膨らむためであり、
// BFF の GET /bff/conversion/jobs/{id}/figures/{figureId}/image が別途配信する（IADR-0154 決定 2）。
public record ConversionFigureDto(
    // 原本から抽出したときの図 ID。ジョブ内で一意。
    string FigureId,
    // LLM による PlantUML / Mermaid 化に成功したか。false は画像保持へ縮退した図である。
    bool Coded,
    // コード化済み・補正済みのときの言語（plantuml / mermaid）。縮退したままなら null。
    string? Language,
    // コード化済み・補正済みのときのコード片。縮退したままなら null。
    string? Code,
    // 縮退した図の画像の参照 URI（storage://…）。コード化に成功した図は画像を保持しないため null。
    string? ImageUri,
    string? ImageContentType,
    // キャプション・近傍テキスト（コード化のヒント）。人手補正時の手がかりとして画面へ渡す。
    string? Caption,
    // 人手補正で Code が入ったか。**自動コード化（Coded == true）と区別する**——
    // 「補正あり」の標識と、再変換で失われる補正の件数がここから導出される（IADR-0154 決定 4）。
    bool Corrected = false,
    DateTimeOffset? CorrectedAt = null);

// FR-12, UC-06, SC-07, IADR-0154: 人手補正の投稿要求。
// **Phase 1 が受け取るのは図のコード片だけである**——変換結果 Markdown 全体の編集は Phase 2 とする
// （05_screens:330。利用者裁定 2026-08-05・質問票 第12回 Q31）。
public record FigureCorrectionRequest(string Language, string Code);

// FR-12, UC-06, SC-07, IADR-0154: 人手補正の投稿結果。
// 置換後の本文 URI を返し、画面が変換結果（SC-03）へ導線を張れるようにする。
public record FigureCorrectionResultDto(string FigureId, string MarkdownUri, int CorrectedFigures);
