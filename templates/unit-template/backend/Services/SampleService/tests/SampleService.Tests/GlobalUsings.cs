// テストプロジェクトの global using（本リポジトリの全テストプロジェクトが同じ 1 行を持つ）。
// ImplicitUsings は Xunit を含まないため、これが無いと [Fact] / IClassFixture<> が
// CS0246 で解決できずビルドが落ちる。
global using Xunit;
