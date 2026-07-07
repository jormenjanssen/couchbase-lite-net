//
//  DeepDocumentTests.cs
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
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Couchbase.Lite;
using Couchbase.Lite.Internal.Doc;
using Couchbase.Lite.Internal.Serialization;
using Couchbase.Lite.Query;

using Xunit;

namespace Couchbase.Lite.Tests.Json;

// Proves that deeply nested structures (5 levels of mixed dictionaries and
// arrays, plus a 16 level chain) serialize correctly through every JSON code
// path: the raw CouchbaseJson writer, ParseTo, real documents saved to a
// database (ToJSON / SetJSON) and query results.
public sealed class DeepDocumentTests : IDisposable
{
    private readonly Database _db;
    private readonly Collection _coll;

    public DeepDocumentTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cbl-json-tests", Guid.NewGuid().ToString("N"));
        _db = new Database("deepjson", new DatabaseConfiguration { Directory = dir });
        _coll = _db.GetDefaultCollection();
    }

    public void Dispose()
    {
        _db.Delete();
    }

    // Level 1 dict -> level 2 list -> level 3 dict -> level 4 list/dict ->
    // level 5 dict with every supported leaf type
    private static Dictionary<string, object?> MakeFiveLevelTree(bool includeBinary) => new()
    {
        ["level1-string"] = "root \"escaped\" ünïcode <>&",
        ["level1-date"] = new DateTimeOffset(2026, 7, 7, 9, 30, 0, TimeSpan.FromHours(2)),
        ["level2-list"] = new List<object?>
        {
            42L,
            "level2-item",
            new Dictionary<string, object?>
            {
                ["level3-double"] = 2.5,
                ["level3-binary"] = includeBinary ? Encoding.UTF8.GetBytes("deep-binary") : null,
                ["level3-list"] = new List<object?>
                {
                    new List<object?> { 1L, 2.5, null, true },
                    new Dictionary<string, object?>
                    {
                        ["level5"] = new Dictionary<string, object?>
                        {
                            ["leaf-string"] = "bottom \t \n value",
                            ["leaf-long"] = Int64.MaxValue,
                            ["leaf-double"] = -0.000125,
                            ["leaf-bool"] = false,
                            ["leaf-null"] = null
                        }
                    }
                }
            }
        }
    };

    [Fact]
    public void FiveLevelTree_SerializeMatchesReflectionSerializer()
    {
        var tree = MakeFiveLevelTree(includeBinary: true);

        var manual = CouchbaseJson.Serialize(tree);
        var reflection = JsonSerializer.Serialize(tree);

        Assert.Equal(reflection, manual);
    }

    [Fact]
    public void FiveLevelTree_ParseToRoundTrip_IsSemanticallyStable()
    {
        var original = CouchbaseJson.Serialize(MakeFiveLevelTree(includeBinary: true));

        var parsed = DataOps.ParseTo<Dictionary<string, object?>>(original);
        Assert.NotNull(parsed);
        var reserialized = CouchbaseJson.Serialize(parsed);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(reserialized)),
            $"Round trip changed the JSON semantics:\n{original}\nvs\n{reserialized}");

        // Fixpoint after the first round trip
        var reparsed = DataOps.ParseTo<Dictionary<string, object?>>(reserialized);
        Assert.Equal(reserialized, CouchbaseJson.Serialize(reparsed));
    }

    [Fact]
    public void Document_FiveLevels_RoundTripsThroughToJsonAndSetJson()
    {
        // Binary leaves are excluded: Fleece renders raw data leaves in JSON in a
        // non round-trippable way, which is native behavior unrelated to this change
        using (var doc1 = new MutableDocument("deep1")) {
            doc1.SetData(MakeFiveLevelTree(includeBinary: false));
            _coll.Save(doc1);
        }

        using var saved1 = _coll.GetDocument("deep1") ?? throw new InvalidOperationException("deep1 missing");
        var json1 = saved1.ToJSON();

        // Rebuild a second document purely from the JSON (exercises ParseTo +
        // the converters), save it, and compare the stored results
        using (var doc2 = new MutableDocument("deep2")) {
            doc2.SetJSON(json1);
            _coll.Save(doc2);
        }

        using var saved2 = _coll.GetDocument("deep2") ?? throw new InvalidOperationException("deep2 missing");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json1), JsonNode.Parse(saved2.ToJSON())),
            "SetJSON round trip changed the stored document");

        // Navigate to level 5 with typed getters and verify each leaf survived
        var level5 = saved2.GetArray("level2-list")!.GetDictionary(2)!
            .GetArray("level3-list")!.GetDictionary(1)!
            .GetDictionary("level5")!;
        Assert.Equal("bottom \t \n value", level5.GetString("leaf-string"));
        Assert.Equal(Int64.MaxValue, level5.GetLong("leaf-long"));
        Assert.Equal(-0.000125, level5.GetDouble("leaf-double"));
        Assert.False(level5.GetBoolean("leaf-bool"));
        Assert.Null(level5.GetValue("leaf-null"));
        Assert.True(level5.Contains("leaf-null"), "null leaf should exist as an entry");

        // The level 4 heterogeneous list should also be intact
        var level4List = saved2.GetArray("level2-list")!.GetDictionary(2)!
            .GetArray("level3-list")!.GetArray(0)!;
        Assert.Equal(new object?[] { 1L, 2.5, null, true }, level4List.ToList().ToArray());
    }

    [Fact]
    public void QueryResult_FiveLevels_SerializesNestedStructure()
    {
        using (var doc = new MutableDocument("deepquery")) {
            doc.SetData(MakeFiveLevelTree(includeBinary: false));
            _coll.Save(doc);
        }

        using var query = _db.CreateQuery(
            "SELECT `level2-list` FROM _default WHERE META().id = 'deepquery'");
        using var results = query.Execute();
        var row = results.AllResults().Single();

        // Result.ToJSON goes through CouchbaseJson.Serialize; verify the level 5
        // leaves survive query serialization
        var node = JsonNode.Parse(row.ToJSON())!;
        var level5 = node["level2-list"]![2]!["level3-list"]![1]!["level5"]!;
        Assert.Equal("bottom \t \n value", (string?)level5["leaf-string"]);
        Assert.Equal(Int64.MaxValue, (long?)level5["leaf-long"]);
        Assert.Equal(-0.000125, (double?)level5["leaf-double"]);
        Assert.False((bool?)level5["leaf-bool"]);
    }

    [Fact]
    public void SixteenLevelChain_RoundTripsStably()
    {
        // Alternating dict / list chain, 16 containers deep
        object? current = "leaf-at-the-bottom";
        for (var depth = 16; depth > 0; depth--) {
            current = depth % 2 == 0
                ? new List<object?> { current, depth }
                : new Dictionary<string, object?> { [$"level{depth}"] = current, ["depth"] = depth };
        }

        var tree = (Dictionary<string, object?>)current!;
        var json = CouchbaseJson.Serialize(tree);

        Assert.Equal(JsonSerializer.Serialize(tree), json);

        var parsed = DataOps.ParseTo<Dictionary<string, object?>>(json);
        Assert.Equal(json, CouchbaseJson.Serialize(parsed));
    }
}
