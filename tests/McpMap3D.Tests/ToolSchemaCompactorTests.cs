using System.Text.Json;
using McpMap3D.Server;
using Xunit;

namespace McpMap3D.Tests;

public class ToolSchemaCompactorTests
{
    private static string Compact(string schema) =>
        ToolSchemaCompactor.Compact(JsonDocument.Parse(schema).RootElement).GetRawText();

    [Fact]
    public void Compact_OptionalNullableParameter_KeepsOnlyItsType()
    {
        var result = Compact("""
            {"type":"object","properties":{"layer":{"description":"Calque.","type":["string","null"],"default":null}}}
            """);

        Assert.Equal("""{"type":"object","properties":{"layer":{"description":"Calque.","type":"string"}}}""", result);
    }

    [Fact]
    public void Compact_NullableArrayItems_AreCleanedToo()
    {
        var result = Compact("""
            {"type":"object","properties":{"names":{"type":["array","null"],"items":{"type":["string","null"]},"default":null}}}
            """);

        Assert.Equal("""{"type":"object","properties":{"names":{"type":"array","items":{"type":"string"}}}}""", result);
    }

    [Fact]
    public void Compact_KeepsRequiredListAndNonNullDefaults()
    {
        const string schema = """
            {"type":"object","properties":{"handles":{"type":"array","items":{"type":"string"}},"limit":{"type":"integer","default":100},"includeGeometry":{"type":"boolean","default":false}},"required":["handles"]}
            """;

        Assert.Equal(schema.Trim(), Compact(schema));
    }

    [Fact]
    public void Compact_UntypedParameter_IsLeftAsIs()
    {
        const string schema = """{"type":"object","properties":{"value":{"description":"Texte, nombre ou point."}},"required":["value"]}""";

        Assert.Equal(schema, Compact(schema));
    }
}
