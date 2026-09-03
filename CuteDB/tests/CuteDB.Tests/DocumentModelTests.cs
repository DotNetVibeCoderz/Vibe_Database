namespace CuteDB.Tests;

public class CuteIdTests
{
    [Fact]
    public void NewId_IsUniqueAcrossManyCalls()
    {
        var ids = new HashSet<CuteId>();
        for (var i = 0; i < 100_000; i++)
        {
            Assert.True(ids.Add(CuteId.NewId()), "CuteId.NewId produced a duplicate.");
        }
    }

    [Fact]
    public void RoundTripsThroughHexAndBytes()
    {
        var id = CuteId.NewId();

        Assert.Equal(id, CuteId.Parse(id.ToString()));
        Assert.Equal(id, CuteId.Read(id.ToByteArray()));
        Assert.Equal(24, id.ToString().Length);
    }

    [Fact]
    public void SortsInCreationOrder()
    {
        // The big-endian layout exists so that byte order matches time order. Ids minted in
        // sequence must therefore already be sorted.
        var ids = new List<CuteId>();
        for (var i = 0; i < 1000; i++)
        {
            ids.Add(CuteId.NewId());
        }

        var sorted = ids.Order().ToList();
        Assert.Equal(ids, sorted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("0123456789abcdef012345")]
    public void RejectsMalformedText(string text) => Assert.False(CuteId.TryParse(text, out _));
}

public class CuteValueTests
{
    [Fact]
    public void NumbersCompareAcrossRepresentations()
    {
        Assert.True(CuteValueComparer.Equal(CuteValue.Int32(1), CuteValue.Int64(1)));
        Assert.True(CuteValueComparer.Equal(CuteValue.Int32(1), CuteValue.Double(1.0)));
        Assert.True(CuteValueComparer.Equal(CuteValue.Int32(1), CuteValue.Decimal(1.0m)));
        Assert.Equal(
            CuteValueComparer.GetHashCode(CuteValue.Int32(7)),
            CuteValueComparer.GetHashCode(CuteValue.Double(7.0)));
    }

    [Fact]
    public void MissingIsDistinctFromNull()
    {
        var document = CuteDocument.Parse("""{ "explicit": null }""");

        Assert.True(document["explicit"].IsNull);
        Assert.False(document["explicit"].IsMissing);
        Assert.True(document["absent"].IsMissing);
        Assert.False(document["absent"].IsNull);
    }

    [Fact]
    public void TypeOrderingIsTotalAndStable()
    {
        var values = new[]
        {
            CuteValue.Object(new CuteObject()),
            CuteValue.String("b"),
            CuteValue.Int32(5),
            CuteValue.Null,
            CuteValue.Boolean(true),
            CuteValue.Array(new CuteArray()),
            CuteValue.Missing,
            CuteValue.String("a"),
        };

        Array.Sort(values, CuteValueEqualityComparer.Instance);

        Assert.Equal(CuteType.Missing, values[0].Type);
        Assert.Equal(CuteType.Null, values[1].Type);
        Assert.Equal(CuteType.True, values[2].Type);
        Assert.Equal(CuteType.Int32, values[3].Type);
        Assert.Equal("a", values[4].AsString);
        Assert.Equal("b", values[5].AsString);
        Assert.Equal(CuteType.Array, values[6].Type);
        Assert.Equal(CuteType.Object, values[7].Type);
    }

    [Fact]
    public void ObjectEqualityIgnoresFieldOrder()
    {
        var left = new CuteObject().Set("a", 1).Set("b", 2);
        var right = new CuteObject().Set("b", 2).Set("a", 1);

        Assert.True(CuteValueComparer.Equal(CuteValue.Object(left), CuteValue.Object(right)));
        Assert.Equal(0, CuteValueComparer.Compare(CuteValue.Object(left), CuteValue.Object(right)));
        Assert.Equal(
            CuteValueComparer.GetHashCode(CuteValue.Object(left)),
            CuteValueComparer.GetHashCode(CuteValue.Object(right)));
    }

    [Fact]
    public void ObjectPreservesInsertionOrderForRendering()
    {
        var obj = new CuteObject().Set("z", 1).Set("a", 2).Set("m", 3);
        Assert.Equal(["z", "a", "m"], obj.Keys);
    }

    [Fact]
    public void ObjectSwitchesToIndexPastTheThresholdAndStillBehaves()
    {
        // The linear-scan-to-hash switchover happens at 12 fields; both sides of it must agree.
        var obj = new CuteObject();
        for (var i = 0; i < 40; i++)
        {
            obj.Set($"field{i}", i);
        }

        Assert.Equal(40, obj.Count);
        Assert.Equal(39, obj["field39"].AsInt32);
        Assert.True(obj.Remove("field7"));
        Assert.True(obj["field7"].IsMissing);
        Assert.Equal(39, obj.Count);
        Assert.Equal(21, obj["field21"].AsInt32);
    }
}

public class CutePathTests
{
    private static readonly CuteDocument Sample = CuteDocument.Parse(
        """
        {
          "customer": { "name": "Sari", "address": { "city": "Bandung" } },
          "lines": [
            { "sku": "KB-01", "qty": 2 },
            { "sku": "MS-04", "qty": 1 }
          ]
        }
        """);

    [Theory]
    [InlineData("customer.name", "Sari")]
    [InlineData("customer.address.city", "Bandung")]
    [InlineData("lines[0].sku", "KB-01")]
    [InlineData("lines[1].sku", "MS-04")]
    [InlineData("lines[-1].sku", "MS-04")]
    public void ResolvesNestedPaths(string path, string expected)
        => Assert.Equal(expected, Sample[CutePath.Parse(path)].AsString);

    [Theory]
    [InlineData("customer.missing")]
    [InlineData("customer.name.deeper")]
    [InlineData("lines[9].sku")]
    [InlineData("nothing.at.all")]
    public void MissingPathsResolveToMissingRatherThanThrowing(string path)
        => Assert.True(Sample[CutePath.Parse(path)].IsMissing);

    [Fact]
    public void ProjectionFlattensAcrossAnArray()
    {
        var skus = Sample[CutePath.Parse("lines[].sku")];

        Assert.True(skus.IsArray);
        Assert.Equal(2, skus.Count);
        Assert.Equal("KB-01", skus[0].AsString);
        Assert.Equal("MS-04", skus[1].AsString);
    }

    [Fact]
    public void AssignCreatesIntermediateObjects()
    {
        var document = new CuteDocument();
        CutePath.Parse("a.b.c").Assign(document.Root, CuteValue.Int32(42));

        Assert.Equal(42, document[CutePath.Parse("a.b.c")].AsInt32);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.")]
    [InlineData("a[")]
    [InlineData("a[x]")]
    public void RejectsMalformedPaths(string path)
        => Assert.False(CutePath.TryParse(path, out _, out _));
}
