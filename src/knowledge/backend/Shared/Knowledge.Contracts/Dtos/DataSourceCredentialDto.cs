namespace Knowledge.Contracts.Dtos;

// SC-22, FR-01, NFR-18, 計画 ADR-0126 決定 1・4, [[IADR-0501]] 決定 3 (#458 段 S2):
// SC-22 の「データソースの資格情報」の群の成員（BFF ↔ DataSourceService `GET /datasources/credentials`）。
// 🔴 **値も参照の文字列も運ばない。** キー名と供給の事実（`DataSourceCredentialSupplies`）だけである。
public record DataSourceCredentialItemDto(
    Guid Id,
    string Name,
    string SourceType,
    List<DataSourceCredentialPropertyDto> Properties);

public record DataSourceCredentialPropertyDto(string Name, string Supply);

// `PUT /datasources/{id}/credentials/{key}/reference` の応答（配置後の供給の事実）。
public record DataSourceCredentialReferenceResultDto(string Property, string Supply);

// 供給の事実の値集合（DataSourceService の `DataSourceCredentialSupply` と同じ綴り）。
public static class DataSourceCredentialSupplies
{
    /// <summary>自分の正規の参照（`vault:datasource/&lt;ID&gt;#&lt;キー&gt;`）を持つ。画面から書いた値が使われる。</summary>
    public const string Reference = "reference";

    /// <summary>平文、または別の場所を指す参照を持つ。画面から書いた値は使われない（移送は段 S4）。</summary>
    public const string Other = "other";

    /// <summary>値を持たない。画面から書くと参照が置かれる。</summary>
    public const string Absent = "absent";
}
