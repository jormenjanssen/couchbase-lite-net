//
//  SerializeBenchmarks.cs
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
using System.Text;
using System.Text.Json;

using BenchmarkDotNet.Attributes;

using Couchbase.Lite.Internal.Serialization;

namespace Couchbase.Lite.Tests.Benchmarks;

// Compares the buffer strategy of the old CouchbaseJson.Serialize
// (MemoryStream + ToArray + GetString) against the current one
// (ArrayBufferWriter + GetString(WrittenSpan)).  Both use the identical
// CouchbaseJson.WriteValue tree walker, so the difference is purely the
// buffering, which is what the PR review feedback was about.
[MemoryDiagnoser]
[ShortRunJob]
public class SerializeBenchmarks
{
    private Dictionary<string, object?> _queryTree = null!;
    private Dictionary<string, object?> _largeDocument = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Shaped like the JSON query tree XQuery.EncodeAsJSON produces for a
        // typical SELECT with a WHERE clause and an ORDER BY
        _queryTree = new Dictionary<string, object?>
        {
            ["WHAT"] = new List<object?>
            {
                new List<object?> { ".id" },
                new List<object?> { ".value" },
                new List<object?> { ".nested" }
            },
            ["FROM"] = new[] { new Dictionary<string, object?> { ["COLLECTION"] = "_default" } },
            ["WHERE"] = new List<object?> { ">=", new List<object?> { ".value" }, new List<object?> { "$min" } },
            ["ORDER_BY"] = new List<object?> { new List<object?> { ".value" } },
            ["LIMIT"] = 100L
        };

        // A larger nested document, similar to Result.ToJSON on a wide row
        var items = new List<object?>();
        for (var i = 0; i < 50; i++) {
            items.Add(new Dictionary<string, object?>
            {
                ["index"] = (long)i,
                ["name"] = $"item-{i} with some text ünïcode & \"quotes\"",
                ["price"] = i * 1.25,
                ["active"] = i % 2 == 0,
                ["tags"] = new List<object?> { "one", "two", "three" }
            });
        }

        _largeDocument = new Dictionary<string, object?>
        {
            ["type"] = "demo",
            ["created"] = new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero),
            ["items"] = items
        };
    }

    [Benchmark(Baseline = true)]
    public string QueryTree_MemoryStream() => SerializeWithMemoryStream(_queryTree);

    [Benchmark]
    public string QueryTree_ArrayBufferWriter() => CouchbaseJson.Serialize(_queryTree);

    [Benchmark]
    public string LargeDoc_MemoryStream() => SerializeWithMemoryStream(_largeDocument);

    [Benchmark]
    public string LargeDoc_ArrayBufferWriter() => CouchbaseJson.Serialize(_largeDocument);

    // The exact implementation CouchbaseJson.Serialize had before the review
    // feedback (and still has on net462, where ArrayBufferWriter is unavailable)
    private static string SerializeWithMemoryStream(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            CouchbaseJson.WriteValue(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
