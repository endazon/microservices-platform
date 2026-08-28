namespace SampleService.Features.Samples.Create;

// テンプレート: スライスの入力（コマンド）。1 操作 = 1 フォルダ（Endpoint / Command / Handler。IADR-0282）。
public sealed record CreateSample(string Name);
