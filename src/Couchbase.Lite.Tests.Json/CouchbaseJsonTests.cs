//
//  CouchbaseJsonTests.cs
//
//  Copyright (c) 2026 Couchbase, Inc All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Couchbase.Lite;
using Couchbase.Lite.Internal.Doc;
using Couchbase.Lite.Internal.Serialization;

using Xunit;

namespace Couchbase.Lite.Tests.Json;

public class CouchbaseJsonTests
{
    // A representative tree covering the full supported value domain, including
    // characters that require escaping and both integral and floating numbers.
    private static Dictionary<string, object?> MakeTree() => new()
    {
        ["string"] = "hello \"world\" <>& ünïcode \n tab\t",
        ["int"] = 42,
        ["long"] = 9_007_199_254_740_993L,
        ["double"] = 3.25,
        ["negative"] = -17,
        ["bool"] = true,
        ["null"] = null,
        ["date"] = new DateTimeOffset(2026, 7, 7, 12, 34, 56, 789, TimeSpan.FromHours(2)),
        ["bytes"] = Encoding.UTF8.GetBytes("binary!"),
        ["list"] = new List<object?> { 1L, "two", 3.5, false, null },
        ["nested"] = new Dictionary<string, object?>
        {
            ["inner"] = new List<object?> { new Dictionary<string, object?> { ["deep"] = "value" } }
        }
    };

    [Fact]
    public void Serialize_MatchesReflectionSerializer()
    {
        var tree = MakeTree();

        var manual = CouchbaseJson.Serialize(tree);
        var reflection = JsonSerializer.Serialize(tree);

        Assert.Equal(reflection, manual);
    }

    [Fact]
    public void Serialize_WritesMutableObjectsAsJsonContainers()
    {
        var dict = new MutableDictionaryObject();
        dict.SetString("name", "cbl");
        var arr = new MutableArrayObject();
        arr.AddInt(1).AddString("two");
        dict.SetArray("items", arr);

        var json = CouchbaseJson.Serialize(dict);

        Assert.Equal("{\"name\":\"cbl\",\"items\":[1,\"two\"]}", json);
    }

    [Fact]
    public void Serialize_UnsavedBlob_WritesMetadataWithInlineData()
    {
        var blob = new Blob("text/plain", Encoding.UTF8.GetBytes("hello"));

        var json = CouchbaseJson.Serialize(blob);

        Assert.Contains("\"@type\":\"blob\"", json);
        Assert.Contains("\"content_type\":\"text/plain\"", json);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("hello")), json);
    }

    [Fact]
    public void RoundTrip_ParseToThenSerialize_IsStable()
    {
        var original = CouchbaseJson.Serialize(MakeTree());

        var parsed = DataOps.ParseTo<Dictionary<string, object?>>(original);
        Assert.NotNull(parsed);
        var reserialized = CouchbaseJson.Serialize(parsed);

        // Semantic equality: a DateTimeOffset serializes unescaped, but once
        // round-tripped it is a plain string whose '+' the default encoder
        // escapes as "+" (identical to the reflection serializer).
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(reserialized)),
            $"Round trip changed the JSON semantics:\n{original}\nvs\n{reserialized}");

        // After one round trip the representation must reach a fixpoint
        var reparsed = DataOps.ParseTo<Dictionary<string, object?>>(reserialized);
        Assert.Equal(reserialized, CouchbaseJson.Serialize(reparsed));
    }

    [Fact]
    public void ParseTo_InvalidJson_ThrowsCouchbaseLiteException()
    {
        Assert.Throws<CouchbaseLiteException>(() => DataOps.ParseTo<Dictionary<string, object?>>("{not valid json"));
    }

    [Fact]
    public void ParseTo_UnregisteredType_DoesNotReportInvalidJson()
    {
        // Registration failures must surface as-is instead of being disguised
        // as an invalid JSON error (review feedback on the AOT change)
        var ex = Record.Exception(() => DataOps.ParseTo<Uri>("\"http://example.com\""));

        Assert.NotNull(ex);
        Assert.IsNotType<CouchbaseLiteException>(ex);
    }

    [Fact]
    public void Serialize_UnsupportedType_Throws()
    {
        Assert.Throws<ArgumentException>(() => CouchbaseJson.Serialize(new object()));
    }

    [Fact]
    public void SerializeLenient_UnsupportedType_FallsBackToToString()
    {
        var value = new Uri("http://example.com/");

        var result = CouchbaseJson.SerializeLenient(value);

        Assert.Equal(value.ToString(), result);
    }
}
