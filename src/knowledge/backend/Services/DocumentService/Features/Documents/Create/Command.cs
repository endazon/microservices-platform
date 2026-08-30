namespace DocumentService.Features.Documents.Create;

public record CreateDocumentRequest(
    string Title,
    string? OriginalUri,
    string? ContentType,
    Dictionary<string, string>? Attributes,
    List<string>? Tags,
    // FR-21: 文書の**本文**。**任意**であり、既存の登録経路を壊さない（要求文「本文フィールドは任意」）。
    // 既定値つきで**末尾へ**足す —— 途中へ挿すと位置引数の呼び出しが壊れ、既定値が無いと
    // 旧クライアントの要求が必須項目を欠く（IADR-0122 決定 2 と同じ理由）。
    string? Body = null);
