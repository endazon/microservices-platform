using AwesomeAssertions;

namespace Platform.Shared.Kernel.Tests.Unit;

/// <summary>
/// NFR / #455 / 計画 ADR-0030・ADR-0041: 共有カーネルの Result / Error。
/// 受け入れ基準 8「Map / Bind / Tap / Match / Ensure / Combine の成功・失敗の両経路を通る」の写像。
/// </summary>
public class ResultTests
{
    [Fact]
    public void Success_は成功になり値を返す()
    {
        var r = Result<int>.Success(42);
        r.IsSuccess.Should().BeTrue();
        r.IsFailure.Should().BeFalse();
        r.Value.Should().Be(42);
    }

    [Fact]
    public void Failure_は失敗になりエラーを返す()
    {
        var e = Error.NotFound("doc.missing", "文書が無い");
        var r = Result<int>.Failure(e);
        r.IsFailure.Should().BeTrue();
        r.Error.Should().Be(e);
        r.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public void 失敗から_Value_を読むと例外になる()
    {
        var r = Result<int>.Failure(Error.Validation("bad", "不正"));
        var act = () => r.Value;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void 成功から_Error_を読むと例外になる()
    {
        var r = Result<int>.Success(1);
        var act = () => r.Error;
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// 構造体は default で生成され得る。「初期化していない」を成功へ倒すと失敗が黙って消えるため、
    /// default は失敗として扱う（Error.Uninitialized）。
    /// </summary>
    [Fact]
    public void default_は成功として扱われない()
    {
        default(Result<int>).IsSuccess.Should().BeFalse();
        default(Result<int>).Error.Should().Be(Error.Uninitialized);
        default(Result).IsSuccess.Should().BeFalse();
        default(Result).Error.Should().Be(Error.Uninitialized);
    }

    [Fact]
    public void Map_は成功なら値を写す()
        => Result<int>.Success(2).Map(x => x * 3).Value.Should().Be(6);

    [Fact]
    public void Map_は失敗ならエラーを伝播し関数を呼ばない()
    {
        var called = false;
        var e = Error.Conflict("c", "衝突");
        var r = Result<int>.Failure(e).Map<int>(_ => { called = true; return 0; });
        r.IsFailure.Should().BeTrue();
        r.Error.Should().Be(e);
        called.Should().BeFalse();
    }

    [Fact]
    public void Bind_は成功なら次へ進む()
        => Result<int>.Success(5).Bind(x => Result<string>.Success($"v{x}")).Value.Should().Be("v5");

    [Fact]
    public void Bind_は失敗ならエラーを伝播し関数を呼ばない()
    {
        var called = false;
        var e = Error.Forbidden("f", "権限無し");
        var r = Result<int>.Failure(e).Bind(_ => { called = true; return Result<string>.Success("x"); });
        r.IsFailure.Should().BeTrue();
        r.Error.Should().Be(e);
        called.Should().BeFalse();
    }

    [Fact]
    public void 値なし_Bind_は成功なら次へ進み失敗なら伝播する()
    {
        Result.Success().Bind(() => Result.Success()).IsSuccess.Should().BeTrue();

        var e = Error.Unauthorized("u", "未認証");
        var called = false;
        var r = Result.Failure(e).Bind(() => { called = true; return Result.Success(); });
        r.Error.Should().Be(e);
        called.Should().BeFalse();
    }

    [Fact]
    public void Tap_は成功時だけ副作用を起こし自身を返す()
    {
        var seen = 0;
        var ok = Result<int>.Success(7).Tap(x => seen = x);
        ok.Value.Should().Be(7);
        seen.Should().Be(7);

        seen = 0;
        Result<int>.Failure(Error.Validation("v", "不正")).Tap(x => seen = x);
        seen.Should().Be(0);
    }

    [Fact]
    public void Ensure_は条件を満たさない成功を失敗にする()
    {
        var e = Error.Validation("range", "範囲外");
        Result<int>.Success(10).Ensure(x => x > 5, e).IsSuccess.Should().BeTrue();
        Result<int>.Success(1).Ensure(x => x > 5, e).Error.Should().Be(e);
    }

    [Fact]
    public void Ensure_は既に失敗ならそのまま伝播し述語を呼ばない()
    {
        var called = false;
        var original = Error.NotFound("n", "無い");
        var r = Result<int>.Failure(original)
            .Ensure(_ => { called = true; return true; }, Error.Validation("v", "不正"));
        r.Error.Should().Be(original);
        called.Should().BeFalse();
    }

    [Fact]
    public void Match_は成否で分岐する()
    {
        Result<int>.Success(3).Match(x => $"ok{x}", e => $"ng{e.Code}").Should().Be("ok3");
        Result<int>.Failure(Error.Conflict("c", "衝突")).Match(x => $"ok{x}", e => $"ng{e.Code}").Should().Be("ngc");
    }

    [Fact]
    public void Combine_は全部成功なら成功になる()
        => Result.Combine(Result.Success(), Result.Success()).IsSuccess.Should().BeTrue();

    [Fact]
    public void Combine_は最初の失敗を返す()
    {
        var first = Error.Validation("a", "1 番目");
        var second = Error.Validation("b", "2 番目");
        Result.Combine(Result.Success(), Result.Failure(first), Result.Failure(second))
            .Error.Should().Be(first);
    }

    [Fact]
    public async Task MapAsync_と_BindAsync_が成功失敗の両方で働く()
    {
        (await Result<int>.Success(2).MapAsync(x => Task.FromResult(x + 1))).Value.Should().Be(3);

        var e = new Error("x", "失敗");
        (await Result<int>.Failure(e).MapAsync(x => Task.FromResult(x + 1))).Error.Should().Be(e);
        (await Result<int>.Failure(e).BindAsync(x => Task.FromResult(Result<int>.Success(x)))).Error.Should().Be(e);
        (await Result<int>.Success(4).BindAsync(x => Task.FromResult(Result<int>.Success(x * 2)))).Value.Should().Be(8);
    }

    [Fact]
    public async Task 値なし_BindAsync_は失敗を伝播する()
    {
        var e = new Error("y", "失敗");
        (await Result.Failure(e).BindAsync(() => Task.FromResult(Result.Success()))).Error.Should().Be(e);
        (await Result.Success().BindAsync(() => Task.FromResult(Result.Success()))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Discard_は値を捨てて成否だけを残す()
    {
        Result<int>.Success(1).Discard().IsSuccess.Should().BeTrue();
        var e = Error.NotFound("n", "無い");
        Result<int>.Failure(e).Discard().Error.Should().Be(e);
    }

    [Fact]
    public void 等価性は成否と中身で決まる()
    {
        (Result<int>.Success(1) == Result<int>.Success(1)).Should().BeTrue();
        (Result<int>.Success(1) != Result<int>.Success(2)).Should().BeTrue();
        var e = Error.Validation("v", "不正");
        (Result<int>.Failure(e) == Result<int>.Failure(e)).Should().BeTrue();
        (Result.Success() == Result.Success()).Should().BeTrue();
    }

    [Fact]
    public void Result_の_Match_と_Tap_が両経路で働く()
    {
        Result.Success().Match(() => "ok", e => $"ng{e.Code}").Should().Be("ok");
        Result.Failure(Error.Validation("v", "不正")).Match(() => "ok", e => $"ng{e.Code}").Should().Be("ngv");

        var hit = 0;
        Result.Success().Tap(() => hit++);
        Result.Failure(Error.Validation("v", "不正")).Tap(() => hit++);
        hit.Should().Be(1);
    }
}
