namespace WikiService.Domain.Ports;

// 🔴 FR-13, [[IADR-0554]] 決定 5 (#1966): **Wiki.js がページの入力そのものを拒んだ**（再試行しても同じ入力では直らない）ことを表す。
//
// いま写すのは「値が列の長さを超えた」（PostgreSQL の 22001 `value too long for type ...`）だけである。Wiki.js 2.x は DB の例外を
// `responseResult { errorCode: 1, message: "<SQL> - value too long for type character varying(255)" }` に平たくして返し、
// SQLSTATE を運ばないので、判定は message の定型句で行うしかない（`WikiJsGraphQlClient.IsValueTooLong`）。
// 受け口はこれを捕まえ、Warning と計数 `wiki.sync.skipped{reason=value_too_long}` を残して ack する（[[IADR-0541]] と同じ型）。
// 🔴 **transport の失敗・GraphQL errors・他の errorCode はこれへ写さない**（従来どおり `WikiJsSyncException` で再試行へ委ねる）。
public sealed class WikiJsInputRejectedException(string operation, string path, string errorCode, string message)
    : Exception($"Wiki.js pages.{operation} rejected the input for '{path}' (code={errorCode}): {message}")
{
    public string Operation { get; } = operation;
    public string Path { get; } = path;
}
