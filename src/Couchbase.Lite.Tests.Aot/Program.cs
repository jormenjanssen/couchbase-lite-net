//
//  Program.cs
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

// Native AOT smoke test.  Publish with `dotnet publish -c Release` (PublishAot is
// set in the project) and run the produced executable.  Every step exercises a
// code path that used to rely on reflection-based System.Text.Json or other
// AOT-incompatible reflection, so a full pass proves the library functions
// without a JIT.  The project also runs with the reflection-based JSON
// serializer disabled, so any missed call site throws immediately.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Couchbase.Lite;
using Couchbase.Lite.Query;

var dbDir = Path.Combine(Path.GetTempPath(), "cbl-aot-smoke");
if (Directory.Exists(dbDir)) {
    Directory.Delete(dbDir, true);
}

var failures = 0;
var db = default(Database);
var coll = default(Collection);

void Step(string name, Action action)
{
    try {
        action();
        Console.WriteLine($"[PASS] {name}");
    } catch (Exception e) {
        failures++;
        Console.WriteLine($"[FAIL] {name}: {e}");
    }
}

Step("Open database", () =>
{
    db = new Database("aotsmoke", new DatabaseConfiguration { Directory = dbDir });
    coll = db.GetDefaultCollection();
});

if (db == null || coll == null) {
    Console.WriteLine("Cannot continue without a database");
    return 1;
}

Step("Save document with nested values and blob", () =>
{
    using var doc = new MutableDocument("doc1");
    doc.SetString("type", "demo");
    doc.SetInt("value", 42);
    doc.SetDate("created", DateTimeOffset.UtcNow);
    doc.SetValue("nested", new Dictionary<string, object?>
    {
        ["numbers"] = new List<object> { 1L, 2L, 3.5 },
        ["flag"] = true
    });
    doc.SetBlob("attachment", new Blob("text/plain", Encoding.UTF8.GetBytes("hello aot")));
    coll!.Save(doc);
});

Step("Read document back", () =>
{
    using var doc = coll!.GetDocument("doc1") ?? throw new Exception("doc1 missing");
    if (doc.GetInt("value") != 42) {
        throw new Exception("value mismatch");
    }

    var blob = doc.GetBlob("attachment") ?? throw new Exception("blob missing");
    var content = blob.Content ?? throw new Exception("blob content missing");
    if (Encoding.UTF8.GetString(content) != "hello aot") {
        throw new Exception("blob content mismatch");
    }
});

Step("Document ToJSON / SetJSON round trip", () =>
{
    using var doc = coll!.GetDocument("doc1") ?? throw new Exception("doc1 missing");
    var json = doc.ToJSON();
    if (String.IsNullOrEmpty(json)) {
        throw new Exception("empty document JSON");
    }

    using var doc2 = new MutableDocument("doc2");
    doc2.SetJSON(json);
    coll.Save(doc2);

    using var readBack = coll.GetDocument("doc2") ?? throw new Exception("doc2 missing");
    if (readBack.GetValue("nested") == null) {
        throw new Exception("nested value lost in SetJSON round trip");
    }

    if (readBack.GetInt("value") != 42) {
        throw new Exception("value lost in SetJSON round trip");
    }
});

Step("Blob ToJSON", () =>
{
    using var doc = coll!.GetDocument("doc1") ?? throw new Exception("doc1 missing");
    var blob = doc.GetBlob("attachment") ?? throw new Exception("blob missing");
    var json = blob.ToJSON();
    if (!json.Contains("digest")) {
        throw new Exception($"unexpected blob JSON: {json}");
    }
});

Step("Create value index (JSON encoded)", () =>
{
    coll!.CreateIndex("valueIndex", IndexBuilder.ValueIndex(ValueIndexItem.Property("value")));
    if (!coll.GetIndexes().Contains("valueIndex")) {
        throw new Exception("index not present after creation");
    }
});

Step("QueryBuilder query with parameters", () =>
{
    using var query = QueryBuilder
        .Select(SelectResult.Expression(Meta.ID), SelectResult.Property("value"), SelectResult.Property("nested"))
        .From(DataSource.Collection(coll!))
        .Where(Expression.Property("value").GreaterThanOrEqualTo(Expression.Parameter("min")));
    query.Parameters = new Parameters().SetInt("min", 10);

    using var results = query.Execute();
    var rows = results.ToList();

    // doc1 and its SetJSON copy doc2 both match
    if (rows.Count != 2) {
        throw new Exception($"expected 2 rows but got {rows.Count}");
    }

    var resultJson = rows[0].ToJSON();
    if (!resultJson.Contains("\"value\":42")) {
        throw new Exception($"unexpected result JSON: {resultJson}");
    }
});

Step("SQL++ query", () =>
{
    using var query = db!.CreateQuery(
        "SELECT META().id AS id, value FROM _default WHERE value = 42 ORDER BY META().id");
    using var results = query.Execute();
    var rows = results.AllResults();
    if (rows.Count != 2) {
        throw new Exception($"expected 2 rows but got {rows.Count}");
    }

    if (rows[0].GetString("id") != "doc1" || rows[1].GetString("id") != "doc2") {
        throw new Exception("unexpected document ids");
    }
});

Step("Result ToDictionary", () =>
{
    using var query = db!.CreateQuery("SELECT value, nested FROM _default WHERE META().id = 'doc1'");
    using var results = query.Execute();
    var dict = results.AllResults()[0].ToDictionary();
    if (Convert.ToInt64(dict["value"]) != 42L) {
        throw new Exception("unexpected dictionary value");
    }
});

Step("Close and delete database", () =>
{
    db!.Delete();
});

Console.WriteLine(failures == 0 ? "All steps passed" : $"{failures} step(s) FAILED");
return failures;
