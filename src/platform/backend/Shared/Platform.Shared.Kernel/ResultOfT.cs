using CfeResult = CSharpFunctionalExtensions.Result;

namespace Platform.Shared.Kernel;

/// <summary>
/// 値を伴う成否。
/// </summary>
/// <typeparam name="T">成功時に伴う値の型。</typeparam>
/// <remarks>
/// NFR, 計画 ADR-0041 決定 2: 内部実装として CSharpFunctionalExtensions を使うが、
/// 公開面には出さない。差し替えは本ファイルの内部に閉じる。
/// </remarks>
public readonly struct Result<T> : IEquatable<Result<T>>
{
    private readonly CSharpFunctionalExtensions.Result<T, Error> _inner;
    private readonly bool _initialized;

    private Result(CSharpFunctionalExtensions.Result<T, Error> inner)
    {
        _inner = inner;
        _initialized = true;
    }

    /// <summary>成功であるか。<c>default</c> は成功として扱わない。</summary>
    public bool IsSuccess => _initialized && _inner.IsSuccess;

    /// <summary>失敗であるか。</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>成功時の値。失敗時に読むと例外を投げる。</summary>
    /// <exception cref="InvalidOperationException">失敗時に読んだ場合。</exception>
    public T Value => IsSuccess
        ? _inner.Value
        : throw new InvalidOperationException("失敗した Result から Value を読もうとした。");

    /// <summary>失敗の内容。成功時に読むと例外を投げる。</summary>
    /// <exception cref="InvalidOperationException">成功時に読んだ場合。</exception>
    public Error Error => !_initialized
        ? Kernel.Error.Uninitialized
        : _inner.IsFailure
            ? _inner.Error
            : throw new InvalidOperationException("成功した Result から Error を読もうとした。");

    /// <summary>成功を作る。</summary>
    public static Result<T> Success(T value) => new(CfeResult.Success<T, Error>(value));

    /// <summary>失敗を作る。</summary>
    public static Result<T> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(CfeResult.Failure<T, Error>(error));
    }

    /// <summary>成功なら値を写す。失敗ならそのまま伝播する。</summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsFailure ? Result<TOut>.Failure(Error) : Result<TOut>.Success(map(Value));
    }

    /// <summary>成功なら次の <see cref="Result{TOut}"/> を返す処理へ進む。</summary>
    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return IsFailure ? Result<TOut>.Failure(Error) : next(Value);
    }

    /// <summary>成功なら値を伴わない次の処理へ進む。</summary>
    public Result Bind(Func<T, Result> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return IsFailure ? Result.Failure(Error) : next(Value);
    }

    /// <summary>成功なら副作用だけを行い、自身を返す。</summary>
    public Result<T> Tap(Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsSuccess) action(Value);
        return this;
    }

    /// <summary>成功でも条件を満たさなければ失敗にする。</summary>
    public Result<T> Ensure(Func<T, bool> predicate, Error error)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(error);
        if (IsFailure) return this;
        return predicate(Value) ? this : Failure(error);
    }

    /// <summary>成否で分岐して 1 つの値へ畳む。</summary>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(Value) : onFailure(Error);
    }

    /// <summary>成功なら値を写す（非同期）。</summary>
    public async Task<Result<TOut>> MapAsync<TOut>(Func<T, Task<TOut>> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsFailure ? Result<TOut>.Failure(Error) : Result<TOut>.Success(await map(Value).ConfigureAwait(false));
    }

    /// <summary>成功なら次の処理へ進む（非同期）。</summary>
    public async Task<Result<TOut>> BindAsync<TOut>(Func<T, Task<Result<TOut>>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return IsFailure ? Result<TOut>.Failure(Error) : await next(Value).ConfigureAwait(false);
    }

    /// <summary>値を捨てて値を伴わない <see cref="Result"/> にする。</summary>
    public Result Discard() => IsFailure ? Result.Failure(Error) : Result.Success();

    /// <inheritdoc />
    public bool Equals(Result<T> other)
    {
        if (IsSuccess != other.IsSuccess) return false;
        return IsSuccess
            ? EqualityComparer<T>.Default.Equals(Value, other.Value)
            : Equals(Error, other.Error);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Result<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        IsSuccess ? EqualityComparer<T>.Default.GetHashCode(Value!) : Error.GetHashCode();

    /// <summary>2 つの <see cref="Result{T}"/> が等しいか。</summary>
    public static bool operator ==(Result<T> left, Result<T> right) => left.Equals(right);

    /// <summary>2 つの <see cref="Result{T}"/> が等しくないか。</summary>
    public static bool operator !=(Result<T> left, Result<T> right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => IsSuccess ? $"Success({Value})" : $"Failure({Error})";
}
