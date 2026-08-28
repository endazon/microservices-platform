using DataSourceService.Domain.Ports;

namespace DataSourceService.Domain;

// FR-01, IADR-0051: SourceType でコネクタを解決するレジストリ。
// 登録済みの全 IDataSourceConnector を SourceType で索引し、未登録型は null（＝コネクタ未対応・縮退）を返す。
// 新規コネクタは DI 登録するだけで解決対象になる（コア改修不要）。
public sealed class ConnectorRegistry
{
    private readonly IReadOnlyDictionary<string, IDataSourceConnector> _byType;

    public ConnectorRegistry(IEnumerable<IDataSourceConnector> connectors)
        => _byType = connectors.ToDictionary(c => c.SourceType, StringComparer.OrdinalIgnoreCase);

    public IDataSourceConnector? Resolve(string sourceType)
        => _byType.GetValueOrDefault(sourceType);
}
