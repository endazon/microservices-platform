namespace GraphService;

// IADR-0027: WebApplicationFactory<T> 用のマーカー。
// 複数サービスの Program 型が同じテストアセンブリへ集まると global::Program が曖昧になり
// CS0433 で壊れるため、サービスごとに一意な型を置く。
public sealed class GraphServiceTestMarker { }
