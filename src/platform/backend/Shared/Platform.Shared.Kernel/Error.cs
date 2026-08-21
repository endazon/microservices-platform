namespace Platform.Shared.Kernel;

/// <summary>
/// エラーの種別。呼び出し側（Api 層）が HTTP ステータスや ProblemDetails へ写像するために使う。
/// </summary>
/// <remarks>NFR, 計画 ADR-0041: Domain から Api まで一貫したエラー表現を与える。</remarks>
public enum ErrorKind
{
    /// <summary>分類のない失敗。</summary>
    Failure = 0,

    /// <summary>入力が不正である。</summary>
    Validation = 1,

    /// <summary>対象が存在しない。</summary>
    NotFound = 2,

    /// <summary>現在の状態と矛盾する。</summary>
    Conflict = 3,

    /// <summary>認証されていない。</summary>
    Unauthorized = 4,

    /// <summary>認証済みだが権限が無い。</summary>
    Forbidden = 5,
}

/// <summary>
/// 失敗の内容を表す不変の値。
/// </summary>
/// <remarks>
/// NFR, 計画 ADR-0041 決定 2: 本型は <c>Platform.Shared.Kernel</c> の公開面である。
/// 内部実装で使う外部ライブラリ（CSharpFunctionalExtensions）の型を露出させない。
/// </remarks>
public sealed record Error(string Code, string Message, ErrorKind Kind = ErrorKind.Failure)
{
    /// <summary>
    /// 既定値（<c>default</c>）の <see cref="Result"/> / <see cref="Result{T}"/> が返すエラー。
    /// </summary>
    /// <remarks>
    /// 構造体は <c>default</c> で生成され得るため、初期化されていない状態を**成功として扱わない**。
    /// 「初期化していない」と「成功した」が同じ値になると、失敗が黙って成功へ倒れる。
    /// </remarks>
    public static readonly Error Uninitialized = new(
        "kernel.uninitialized",
        "Result が初期化されていない（default で生成された）。",
        ErrorKind.Failure);

    /// <summary>入力が不正であることを示すエラーを作る。</summary>
    public static Error Validation(string code, string message) => new(code, message, ErrorKind.Validation);

    /// <summary>対象が存在しないことを示すエラーを作る。</summary>
    public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);

    /// <summary>現在の状態と矛盾することを示すエラーを作る。</summary>
    public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);

    /// <summary>認証されていないことを示すエラーを作る。</summary>
    public static Error Unauthorized(string code, string message) => new(code, message, ErrorKind.Unauthorized);

    /// <summary>権限が無いことを示すエラーを作る。</summary>
    public static Error Forbidden(string code, string message) => new(code, message, ErrorKind.Forbidden);

    /// <inheritdoc />
    public override string ToString() => $"{Kind}:{Code}: {Message}";
}
