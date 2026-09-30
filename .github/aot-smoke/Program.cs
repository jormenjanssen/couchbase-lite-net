using Couchbase.Lite;
using Couchbase.Lite.Query;

// Exercises the code paths that used reflection-based System.Text.Json or DI scanning before #1779:
// database/service startup, JSON document construction, ToJSON, and query encoding with parameters.
var directory = Path.Combine(Path.GetTempPath(), "cbl-aot-smoke-" + Guid.NewGuid().ToString("N"));
var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition)
        failures.Add(message);
}

try
{
    using var database = new Database("smoke", new DatabaseConfiguration { Directory = directory });
    var collection = database.GetDefaultCollection();

    using (var document = new MutableDocument("doc-1"))
    {
        var tags = new MutableArrayObject();
        tags.AddString("a").AddString("b");
        var nested = new MutableDictionaryObject();
        nested.SetLong("seq", 5_000_000_001L);
        document.SetString("name", "smoke")
            .SetInt("count", 42)
            .SetDouble("ratio", 0.5)
            .SetArray("tags", tags)
            .SetDictionary("nested", nested);
        collection.Save(document);
    }

    using (var jsonDocument = new MutableDocument("doc-2", "{\"name\":\"json\",\"count\":7,\"nested\":{\"seq\":3}}"))
    {
        collection.Save(jsonDocument);
    }

    using (var stored = collection.GetDocument("doc-1"))
    {
        Check(stored != null, "doc-1 not found");
        Check(stored?.GetDictionary("nested")?.GetLong("seq") == 5_000_000_001L, "nested long did not round-trip");
        Check(stored?.ToJSON().Contains("\"smoke\"", StringComparison.Ordinal) == true, "ToJSON misses the name");
    }

    using (var query = QueryBuilder.Select(SelectResult.Property("name"), SelectResult.Property("count"))
               .From(DataSource.Collection(collection))
               .Where(Expression.Property("count").GreaterThan(Expression.Parameter("min")))
               .OrderBy(Ordering.Property("count")))
    {
        query.Parameters = new Parameters().SetInt("min", 5);
        var rows = query.Execute().AllResults();
        Check(rows.Count == 2, $"builder query returned {rows.Count} rows, expected 2");
        Check(rows.Count > 0 && rows[0].GetString("name") == "json", "builder query ordering is wrong");
        Check(rows.Count > 0 && rows[0].ToJSON().Contains("\"json\"", StringComparison.Ordinal), "Result.ToJSON misses the name");
    }

    using (var sqlQuery = database.CreateQuery("SELECT name FROM _ WHERE count > $min ORDER BY count"))
    {
        sqlQuery.Parameters = new Parameters().SetInt("min", 10);
        var rows = sqlQuery.Execute().AllResults();
        Check(rows.Count == 1 && rows[0].GetString("name") == "smoke", "SQL++ query with parameter failed");
    }

    database.Close();
}
catch (Exception ex)
{
    failures.Add(ex.ToString());
}
finally
{
    try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

if (failures.Count != 0)
{
    Console.Error.WriteLine("AOT smoke test FAILED:");
    foreach (var failure in failures)
        Console.Error.WriteLine(" - " + failure);
    return 1;
}

Console.WriteLine($"AOT smoke test passed ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}).");
return 0;
