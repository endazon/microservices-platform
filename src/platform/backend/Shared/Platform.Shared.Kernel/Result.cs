using CfeUnitResult = CSharpFunctionalExtensions.UnitResult<Platform.Shared.Kernel.Error>;

namespace Platform.Shared.Kernel;

/// <summary>
/// 値を伴わない成否。
/// </summary>
/// <remarks>
/// NFR, 計画 ADR-0041 決定 2: 内部実装として CSharpFunctionalExtensions を使うが、
/// **公開面（引数・戻り値・例外の型）には一切出さない**。型エイリアスで包むだけでは
/// 拡張メソッドと Bind / Map のチェーンから外部 API が漏れるため、自前の型で包む。
/// </remarks>
public readonly struct Result : IEquatable<Result>
{
    private readonly CfeUnitResult _inner;
    private readonly bool _initialized;

    private Result(CfeUnitResult inner)
    {
        _inner = inner;
        _initialized = true;
    }

    /// <summary>成功であるか。<c>default</c> は成功として扱わない。</summary>
    public bool IsSuccess => _initialized && _inner.IsSuccess;

    /// <summary>失敗であるか。</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>失敗の内容。成功時に読むと例外を投げる。</summary>
    /// <exception cref="InvalidOperationException">成功時に読んだ場合。</exception>
    public Error Error => !_initialized
        ? Kernel.Error.Uninitialized
        : _inner.IsFailure
            ? _inner.Error
            : throw new InvalidOperationException("成功した Result から Error を読もうとした。");

    /// <summary>成功を作る。</summary>
    public static Result Success() => new(CSharpFunctionalExtensions.UnitResult.Success<Error>());

    /// <summary>失敗を作る。</summary>
    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(CSharpFunctionalExtensions.UnitResult.Failure(error));
    }

    /// <summary>成功なら次の処理へ進む。失敗ならそのまま伝播する。</summary>
    public Result Bind(Func<Result> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return IsFailure ? this : next();
    }

    /// <summary>成功なら値を伴う処理へ進む。失敗ならそのまま伝播する。</summary>
    public Result<TOut> Bind<TOut>(Func<Result<TOut>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return IsFailure ? Result<TOut>.Failure(Error) : next();
    }

    /// <summary>成功なら副作用だけを行い、自身を返す。</summary>
    public Result Tap(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsSuccess) action();
        return this;
    }

    /// <summary>成否で分岐して 1 つの値へ畳む。</summary>
    public TOut Match<TOut>(Func<TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess() : onFailure(Error);
    }

    /// <summary>成功なら次の処理へ進む（非同期）。</summary>
    public async Task<Result> BindAsync(Func<Task<Result>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return IsFailure ? this : await next().ConfigureAwait(false);
    }

    /// <summary>すべて成功なら成功を、1 つでも失敗があれば**最初の失敗**を返す。</summary>
    public static Result Combine(params Result[] results)
    {
        ArgumentNullException.ThrowIfNull(results);
        foreach (var r in results)
        {
            if (r.IsFailure) return r;
        }

        return Success();
    }

    /// <inheritdoc />
    public bool Equals(Result other) =>
        IsSuccess == other.IsSuccess && (IsSuccess || Equals(Error, other.Error));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Result other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => IsSuccess ? 0 : Error.GetHashCode();

    /// <summary>2 つの <see cref="Result"/> が等しいか。</summary>
    public static bool operator ==(Result left, Result right) => left.Equals(right);

    /// <summary>2 つの <see cref="Result"/> が等しくないか。</summary>
    public static bool operator !=(Result left, Result right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => IsSuccess ? "Success" : $"Failure({Error})";
}
