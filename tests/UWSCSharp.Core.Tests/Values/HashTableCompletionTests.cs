using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class HashTableCompletionTests
{
    [Theory]
    [InlineData("x[]")]
    [InlineData("x")]
    public async Task ValueParametersKeepCallerUnchanged(string formal)
    {
        Assert.Equal(["7", "1"], await Run($$"""
            HASHTBL h
            h["a"]=1
            F(h)
            PRINT h["a"]
            PROCEDURE F({{formal}})
                x["a"]=7
                PRINT x["a"]
            FEND
            """));
    }

    [Theory]
    [InlineData("x[]", "1")]
    [InlineData("VAR x[]", "7")]
    public async Task NormalExitCopiesOnlyExplicitVar(
        string formal, string expected)
    {
        Assert.Equal([expected], await Run($$"""
            HASHTBL h
            h["a"]=1
            F(h)
            PRINT h["a"]
            PROCEDURE F({{formal}})
                x["a"]=7
                EXIT
            FEND
            """));
    }

    [Fact]
    public async Task MixedAliasesCopyOnlyVarParameter()
    {
        Assert.Equal(["1", "1", "8", "7"], await Run("""
            HASHTBL h
            h["a"]=1
            F(h,h,h)
            PRINT h["a"]
            PROCEDURE F(first[],VAR second[],third[])
                first["a"]=9
                PRINT second["a"]
                second["a"]=7
                PRINT third["a"]
                third["a"]=8
                PRINT third["a"]
            FEND
            """));
    }

    [Theory]
    [InlineData("HASH_KEY")]
    [InlineData("HASH_VAL")]
    public async Task EmptyOrdinalIsRejectedWithoutChangingTable(
        string mode)
    {
        Assert.Equal(["error", "a", "7", "1"], await Run($$"""
            HASHTBL h
            h["a"]=7
            TRY
                PRINT h[EMPTY,{{mode}}]
            EXCEPT
                PRINT "error"
            ENDTRY
            PRINT h[0,HASH_KEY]
            PRINT h[0,HASH_VAL]
            PRINT LENGTH(h)
            """));
    }

    [Fact]
    public async Task EmptyKeyStillNamesEmptyString()
    {
        Assert.Equal(["7", "1", "1", "0"], await Run("""
            HASHTBL h
            h[EMPTY]=7
            PRINT h[""]
            PRINT h[EMPTY,HASH_EXISTS]
            PRINT h[EMPTY,HASH_REMOVE]
            PRINT LENGTH(h)
            """));
    }

    [Fact]
    public void StoredValuesKeepKindsMetadataAndObjectIdentity()
    {
        object instance = new();
        Value[] values =
        [
            Value.Empty,
            new(ValueKind.Null),
            Value.Nothing,
            Value.FromError(unchecked((int)0x80004005)),
            Value.Numeric(1) with
            {
                NativeVariantType = 11,
                NativeScalar = true
            },
            Value.String("日本語"),
            Value.FromObject(instance),
            Value.FromArray(UwscArray.Create([0], [Value.Numeric(7)])),
            Value.FromHashTable(UwscHashTable.Create())
        ];
        var table = UwscHashTable.Create();
        foreach (Value value in values)
        {
            table = table.Set(value.Kind.ToString(), value);
        }

        foreach (Value value in values)
        {
            Value stored = table.Get(value.Kind.ToString());
            Assert.Equal(value, stored);
            Assert.Equal(value.NativeVariantType, stored.NativeVariantType);
            Assert.Same(value.NativeScalar, stored.NativeScalar);
        }

        Assert.Same(instance, table.Get("Object").AsObject());
    }

    [Fact]
    public void KeySnapshotsStayIndependentAfterUpdates()
    {
        UwscHashTable original = UwscHashTable.Create(8192)
            .Set("b", Value.Numeric(1)).Set("a", Value.Numeric(2));
        IReadOnlyList<string> snapshot = original.Keys();
        UwscHashTable updated = original.Remove("A")
            .Set("c", Value.Numeric(3));

        Assert.Equal(["a", "b"], snapshot);
        Assert.Equal(["b", "c"], updated.Keys());
        Assert.Equal(["a", "b"], original.Keys());
        Assert.Equal(8192, updated.Clear().Flags);
    }
}

[Trait("Category", "Headless")]
public sealed class HashTableCapacityTests
{
    [Fact]
    public void LimitAllowsReplacementRemovalAndClear()
    {
        var table = UwscHashTable.Create();
        for (int index = 0; index < UwscArray.MaximumElements; index++)
        {
            table = table.Set(index.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
                Value.Numeric(index));
        }

        Assert.Equal(UwscArray.MaximumElements, table.Count);
        Assert.Throws<InvalidOperationException>(() =>
            table.Set("overflow", Value.Empty));
        UwscHashTable replaced = table.Set("0", Value.Numeric(7));
        Assert.Equal(UwscArray.MaximumElements, replaced.Count);
        Assert.Equal(Value.Numeric(0), table.Get("0"));
        Assert.Equal(Value.Numeric(7), replaced.Get("0"));
        UwscHashTable reused = replaced.Remove("0")
            .Set("new", Value.Empty);
        Assert.Equal(UwscArray.MaximumElements, reused.Count);
        Assert.Equal("new", reused.KeyAt(reused.Count - 1));
        Assert.Equal(0, reused.Clear().Count);
    }
}
