namespace CuteDB.Tests;

public class BinaryFormatTests
{
    /// <summary>One value of every storable type, shared by the round-trip and skip tests.</summary>
    private static CuteValue[] SampleValues() =>
    [
        CuteValue.Null,
        CuteValue.Boolean(true),
        CuteValue.Boolean(false),
        CuteValue.Int32(0),
        CuteValue.Int32(int.MinValue),
        CuteValue.Int64(long.MaxValue),
        CuteValue.Double(3.141592653589793),
        CuteValue.Double(double.NegativeInfinity),
        CuteValue.Decimal(1234567.891m),
        CuteValue.Decimal(-0.0000001m),
        CuteValue.String(string.Empty),
        CuteValue.String("halo dunia"),
        CuteValue.String("emoji 🎉 dan aksara ᮘᮞ"),
        CuteValue.Binary([1, 2, 3, 250, 255]),
        CuteValue.DateTime(new DateTime(2026, 2, 14, 9, 30, 0, DateTimeKind.Utc)),
        CuteValue.Guid(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff")),
        CuteValue.Id(CuteId.NewId()),
        CuteValue.ArrayOf(CuteValue.Int32(1), CuteValue.String("two"), CuteValue.Null),
    ];

    [Fact]
    public void RoundTripsEveryValueType()
    {
        foreach (var value in SampleValues())
        {
            var decoded = CuteBinary.Decode(CuteBinary.Encode(value));

            Assert.Equal(value.Type, decoded.Type);
            Assert.True(CuteValueComparer.Equal(value, decoded), $"{value} did not survive the round trip.");
        }
    }

    [Fact]
    public void RoundTripsADeeplyNestedDocument()
    {
        var json = """
            {
              "order": "SO-2026-0042",
              "customer": { "name": "Budi", "tiers": ["gold", "wholesale"],
                            "address": { "city": "Surabaya", "geo": { "lat": -7.25, "lng": 112.75 } } },
              "lines": [
                { "sku": "KB-01", "qty": 2, "price": 189000, "tags": [] },
                { "sku": "MS-04", "qty": 1, "price": 99000, "tags": ["clearance"] }
              ],
              "paid": true,
              "note": null
            }
            """;

        var original = CuteJson.Parse(json);
        var decoded = CuteBinary.Decode(CuteBinary.Encode(original));

        Assert.True(CuteValueComparer.Equal(original, decoded));
        Assert.Equal("Surabaya", decoded["customer"]["address"]["city"].AsString);
        Assert.Equal(-7.25, decoded["customer"]["address"]["geo"]["lat"].AsDouble);
    }

    [Fact]
    public void SkipReportsTheSameLengthReadConsumes()
    {
        foreach (var value in SampleValues())
        {
            var encoded = CuteBinary.Encode(value);
            CuteBinary.Read(encoded, out var consumed);

            Assert.Equal(consumed, CuteBinary.Skip(encoded));
            Assert.Equal(encoded.Length, consumed);
        }
    }

    [Fact]
    public void FieldLookupWalksEncodedBytesWithoutDecoding()
    {
        var document = CuteJson.Parse("""
            { "a": 1, "big": { "x": [1,2,3,4,5], "y": "a long string that would cost something to decode" },
              "target": "found", "z": 9 }
            """);

        var encoded = CuteBinary.Encode(document);
        var path = CutePath.Parse("target");

        Assert.Equal("found", path.ResolveEncoded(encoded).AsString);
    }

    [Fact]
    public void EncodedAndDecodedPathResolutionAgree()
    {
        var document = CuteJson.Parse("""
            { "customer": { "address": { "city": "Medan" } },
              "lines": [ { "sku": "A" }, { "sku": "B" } ],
              "n": null }
            """);

        var encoded = CuteBinary.Encode(document);

        foreach (var text in new[]
                 {
                     "customer", "customer.address", "customer.address.city", "customer.address.zip",
                     "lines", "lines[0]", "lines[1].sku", "lines[5].sku", "lines[].sku", "n", "nope",
                 })
        {
            var path = CutePath.Parse(text);
            var fromTree = path.Resolve(document);
            var fromBytes = path.ResolveEncoded(encoded);

            Assert.Equal(fromTree.Type, fromBytes.Type);
            Assert.True(CuteValueComparer.Equal(fromTree, fromBytes), $"Path '{text}' disagreed.");
        }
    }

    [Fact]
    public void RejectsTruncatedInput()
    {
        var encoded = CuteBinary.Encode(CuteJson.Parse("""{ "a": [1, 2, 3] }"""));
        Assert.Throws<CuteCorruptionException>(() => CuteBinary.Decode(encoded.AsSpan(0, encoded.Length - 3)));
    }
}

public class JsonTests
{
    [Fact]
    public void ParsesIntegersIntoTheNarrowestType()
    {
        var value = CuteJson.Parse("""{ "small": 5, "big": 5000000000, "float": 1.5 }""");

        Assert.Equal(CuteType.Int32, value["small"].Type);
        Assert.Equal(CuteType.Int64, value["big"].Type);
        Assert.Equal(CuteType.Double, value["float"].Type);
    }

    [Fact]
    public void FinancialOptionsReadFractionsAsDecimal()
    {
        var value = CuteJson.Parse("""{ "price": 0.1, "qty": 3 }""", CuteJsonOptions.Financial);

        Assert.Equal(CuteType.Decimal, value["price"].Type);
        Assert.Equal(CuteType.Int32, value["qty"].Type);
        Assert.Equal(0.1m, value["price"].AsDecimal);
    }

    [Fact]
    public void LosslessOptionsRoundTripEveryType()
    {
        var original = new CuteObject()
            .Set("when", CuteValue.DateTime(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)))
            .Set("who", CuteValue.Guid(Guid.NewGuid()))
            .Set("id", CuteValue.Id(CuteId.NewId()))
            .Set("money", CuteValue.Decimal(19999.95m))
            .Set("blob", CuteValue.Binary([9, 8, 7]));

        var json = CuteJson.Write(CuteValue.Object(original), CuteJsonOptions.Lossless);
        var parsed = CuteJson.Parse(json, CuteJsonOptions.Lossless);

        Assert.True(CuteValueComparer.Equal(CuteValue.Object(original), parsed), json);
    }

    [Fact]
    public void PlainJsonStaysReadable()
    {
        var value = new CuteObject()
            .Set("name", "Kedai Kopi")
            .Set("open", true)
            .Set("rating", 4.8);

        var json = CuteJson.Write(CuteValue.Object(value));

        Assert.Equal("""{"name":"Kedai Kopi","open":true,"rating":4.8}""", json);
    }

    [Fact]
    public void NonFiniteDoublesBecomeNullRatherThanInvalidJson()
    {
        var value = new CuteObject().Set("nan", CuteValue.Double(double.NaN));
        var json = CuteJson.Write(CuteValue.Object(value));

        Assert.Equal("""{"nan":null}""", json);
    }

    [Fact]
    public void AFieldNamedLikeATagIsNotMistakenForOne()
    {
        // A one-field object whose key starts with '$' is only reinterpreted when the value
        // actually parses as that type. Anything else stays the object it was.
        var parsed = CuteJson.Parse("""{ "$date": "not a date at all" }""");

        Assert.True(parsed.IsObject);
        Assert.Equal("not a date at all", parsed["$date"].AsString);
    }
}
