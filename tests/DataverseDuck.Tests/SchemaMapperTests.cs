using System.Data;
using MarkMpn.Sql4Cds.Engine;
using DataverseDuck.Schema;
using DuckDB.NET.Data;
using Microsoft.Xrm.Sdk;

namespace DataverseDuck.Tests;

public class SchemaMapperTests
{
    private readonly DataverseSchemaMapper _mapper = new();

    [Theory]
    // These CLR types are the ones SQL 4 CDS actually produces, taken from its
    // own SqlTypeConverter.SqlToNetType mapping rather than assumed.
    [InlineData(typeof(bool), "BOOLEAN")]
    [InlineData(typeof(byte), "UTINYINT")]
    [InlineData(typeof(short), "SMALLINT")]
    [InlineData(typeof(int), "INTEGER")]
    [InlineData(typeof(long), "BIGINT")]
    [InlineData(typeof(float), "FLOAT")]
    [InlineData(typeof(double), "DOUBLE")]
    [InlineData(typeof(Guid), "UUID")]
    [InlineData(typeof(string), "VARCHAR")]
    [InlineData(typeof(byte[]), "BLOB")]
    [InlineData(typeof(TimeSpan), "TIME")]
    [InlineData(typeof(DateTime), "TIMESTAMP")]
    public void Clr_types_map_to_duckdb_types(Type clrType, string expected)
    {
        Assert.Equal(expected, _mapper.ToDuckDbType(clrType));
    }

    [Fact]
    public void DateTimeOffset_maps_to_naive_TIMESTAMP_not_TIMESTAMPTZ()
    {
        // A datetimeoffset column surfaces as DateTimeOffset. Storing it as
        // TIMESTAMPTZ would make comparisons session-dependent (ADR 0002).
        Assert.Equal("TIMESTAMP", _mapper.ToDuckDbType(typeof(DateTimeOffset)));
    }

    [Fact]
    public void Nullable_types_map_to_their_underlying_type()
    {
        Assert.Equal("INTEGER", _mapper.ToDuckDbType(typeof(int?)));
        Assert.Equal("TIMESTAMP", _mapper.ToDuckDbType(typeof(DateTime?)));
    }

    [Fact]
    public void Sdk_wrapper_types_are_handled_even_though_the_reader_folds_them()
    {
        // SQL 4 CDS converts these before the reader sees them, but values
        // arriving straight from IOrganizationService still carry them.
        Assert.Equal("INTEGER", _mapper.ToDuckDbType(typeof(OptionSetValue)));
        Assert.Equal("DECIMAL(38,10)", _mapper.ToDuckDbType(typeof(Money)));
    }

    [Fact]
    public void An_unmapped_type_fails_loudly_rather_than_guessing()
    {
        var error = Assert.Throws<NotSupportedException>(() => _mapper.ToDuckDbType(typeof(Uri)));
        Assert.Contains("Uri", error.Message);
    }

    [Fact]
    public void A_lookup_becomes_a_joinable_uuid_plus_its_target_table()
    {
        var columns = _mapper.MapColumn("primarycontactid", typeof(EntityReference), 3).ToList();

        Assert.Equal(2, columns.Count);
        Assert.Equal(("primarycontactid", "UUID", ColumnKind.LookupId), Describe(columns[0]));
        Assert.Equal(("primarycontactid_entitytype", "VARCHAR", ColumnKind.LookupTargetTable), Describe(columns[1]));

        // Both read from the same reader ordinal.
        Assert.All(columns, c => Assert.Equal(3, c.SourceColumn));
    }

    [Fact]
    public void The_lookup_target_column_can_be_suppressed()
    {
        var mapper = new DataverseSchemaMapper { IncludeLookupTargetTable = false };

        var column = Assert.Single(mapper.MapColumn("ownerid", typeof(EntityReference), 0));
        Assert.Equal("UUID", column.DuckDbType);
    }

    [Fact]
    public void The_engines_own_lookup_type_maps_the_same_as_the_sdks()
    {
        // A live query returns SqlEntityReference where the fakes returned
        // EntityReference. The DDL must not depend on which one turned up.
        var sdk = _mapper.MapColumn("primarycontactid", typeof(EntityReference), 3).ToList();
        var engine = _mapper.MapColumn("primarycontactid", typeof(SqlEntityReference), 3).ToList();

        Assert.Equal(sdk.Select(Describe), engine.Select(Describe));
        Assert.Equal("UUID", _mapper.ToDuckDbType(typeof(SqlEntityReference)));
    }

    [Fact]
    public void A_nullable_engine_lookup_is_still_a_lookup()
    {
        // It is a struct, so unlike EntityReference it can arrive as
        // SqlEntityReference? and must be unwrapped before the type test.
        var columns = _mapper.MapColumn("ownerid", typeof(SqlEntityReference?), 0).ToList();

        Assert.Equal(2, columns.Count);
        Assert.Equal(ColumnKind.LookupId, columns[0].Kind);
        Assert.Equal(ColumnKind.LookupTargetTable, columns[1].Kind);
    }

    private static (string, string, ColumnKind) Describe(ColumnMapping c) => (c.Name, c.DuckDbType, c.Kind);
}

public class DuckDbIdentifierTests
{
    [Fact]
    public void Identifiers_are_quoted()
    {
        Assert.Equal("\"account\"", DuckDbIdentifier.Quote("account"));
    }

    [Fact]
    public void Embedded_quotes_are_doubled_so_ddl_cannot_be_escaped()
    {
        // Table names can come from user input, and DDL is built by string
        // concatenation, so this is the injection boundary.
        Assert.Equal("\"we\"\"ird\"", DuckDbIdentifier.Quote("we\"ird"));
    }

    [Fact]
    public void A_quote_injection_attempt_cannot_break_out_of_the_identifier()
    {
        // Behavioural rather than string-shaped: build DDL from a hostile name
        // and prove the victim table survives.
        using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");

        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE victim (id INTEGER); INSERT INTO victim VALUES (1);";
            setup.ExecuteNonQuery();
        }

        var hostile = "x\"); DROP TABLE victim; --";
        var mapping = new TableMapping(hostile, [new ColumnMapping("id", "INTEGER", 0, ColumnKind.Scalar, typeof(int))]);

        using (var create = connection.CreateCommand())
        {
            create.CommandText = mapping.ToCreateTableSql();
            create.ExecuteNonQuery();
        }

        using var check = connection.CreateCommand();
        check.CommandText = "SELECT count(*) FROM victim";
        Assert.Equal(1L, Convert.ToInt64(check.ExecuteScalar()));

        // And the hostile name became one ordinary table.
        using var named = connection.CreateCommand();
        named.CommandText = $"SELECT count(*) FROM {DuckDbIdentifier.Quote(hostile)}";
        Assert.Equal(0L, Convert.ToInt64(named.ExecuteScalar()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_identifiers_are_rejected(string? identifier)
    {
        Assert.ThrowsAny<ArgumentException>(() => DuckDbIdentifier.Quote(identifier!));
    }
}

public class ValueConversionTests
{
    [Fact]
    public void DateTimeOffset_is_reduced_to_a_utc_instant()
    {
        var value = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.FromHours(2));

        var converted = Assert.IsType<DateTime>(
            DataverseSchemaMapper.ConvertValue(value, ColumnKind.Scalar));

        // The offset is applied, not discarded: 12:00+02:00 is 10:00 UTC.
        Assert.Equal(new DateTime(2026, 8, 16, 10, 0, 0), converted);
    }

    [Fact]
    public void A_lookup_yields_its_id_and_its_target_table()
    {
        var reference = new EntityReference("contact", Guid.Parse("11111111-1111-1111-1111-111111111111"));

        Assert.Equal(reference.Id, DataverseSchemaMapper.ConvertValue(reference, ColumnKind.LookupId));
        Assert.Equal("contact", DataverseSchemaMapper.ConvertValue(reference, ColumnKind.LookupTargetTable));
    }

    [Fact]
    public void A_bare_guid_lookup_has_no_known_target_table()
    {
        // What ReturnEntityReferenceAsGuid produces. The id still works; the
        // target table is genuinely unknown rather than wrong.
        var id = Guid.NewGuid();

        Assert.Equal(id, DataverseSchemaMapper.ConvertValue(id, ColumnKind.LookupId));
        Assert.Null(DataverseSchemaMapper.ConvertValue(id, ColumnKind.LookupTargetTable));
    }

    // The engine returns its own SqlEntityReference, not the SDK's
    // EntityReference. Every test before the first live run fed the mapper SDK
    // types, because that is what a fake IOrganizationService produces, so this
    // whole shape went untested until a real tenant rejected it.
    [Fact]
    public void The_engines_own_lookup_type_yields_its_id_and_target_table()
    {
        var id = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var reference = new SqlEntityReference("offline", "contact", id);

        Assert.Equal(id, DataverseSchemaMapper.ConvertValue(reference, ColumnKind.LookupId));
        Assert.Equal("contact", DataverseSchemaMapper.ConvertValue(reference, ColumnKind.LookupTargetTable));
    }

    [Fact]
    public void A_null_engine_lookup_stores_null_rather_than_an_empty_guid()
    {
        // SqlEntityReference is a struct, so an absent lookup arrives as a
        // non-null value with IsNull set rather than as DBNull. Reading .Id
        // unconditionally would store Guid.Empty: a row that silently claims to
        // point at nothing instead of admitting it points nowhere.
        var absent = SqlEntityReference.Null;

        Assert.True(absent.IsNull);
        Assert.Null(DataverseSchemaMapper.ConvertValue(absent, ColumnKind.LookupId));
        Assert.Null(DataverseSchemaMapper.ConvertValue(absent, ColumnKind.LookupTargetTable));
    }

    [Fact]
    public void An_engine_lookup_in_a_scalar_column_still_reduces_to_its_id()
    {
        var id = Guid.Parse("33333333-3333-3333-3333-333333333333");

        Assert.Equal(
            id,
            DataverseSchemaMapper.ConvertValue(
                new SqlEntityReference("offline", "account", id), ColumnKind.Scalar));

        Assert.Null(DataverseSchemaMapper.ConvertValue(SqlEntityReference.Null, ColumnKind.Scalar));
    }

    [Fact]
    public void Sdk_wrappers_are_unwrapped()
    {        Assert.Equal(3, DataverseSchemaMapper.ConvertValue(new OptionSetValue(3), ColumnKind.Scalar));
        Assert.Equal(12.34m, DataverseSchemaMapper.ConvertValue(new Money(12.34m), ColumnKind.Scalar));
    }

    [Fact]
    public void Nulls_and_DBNull_both_become_null()
    {
        Assert.Null(DataverseSchemaMapper.ConvertValue(null, ColumnKind.Scalar));
        Assert.Null(DataverseSchemaMapper.ConvertValue(DBNull.Value, ColumnKind.Scalar));
        Assert.Null(DataverseSchemaMapper.ConvertValue(DBNull.Value, ColumnKind.LookupId));
    }

    [Fact]
    public void A_non_lookup_value_in_a_lookup_column_fails_loudly()
    {
        Assert.Throws<InvalidOperationException>(
            () => DataverseSchemaMapper.ConvertValue("not a lookup", ColumnKind.LookupId));
    }
}

public class BulkLoaderTests
{
    /// <summary>
    /// Stands in for the SQL 4 CDS reader, producing the CLR types it produces.
    /// </summary>
    private static DataTableReader BuildReader()
    {
        var table = new DataTable();
        table.Columns.Add("accountid", typeof(Guid));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("revenue", typeof(decimal));
        table.Columns.Add("createdon", typeof(DateTime));
        table.Columns.Add("primarycontactid", typeof(EntityReference));

        table.Rows.Add(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            "Fourth Coffee",
            1234.56m,
            new DateTime(2026, 8, 16, 10, 30, 0, DateTimeKind.Utc),
            new EntityReference("contact", Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001")));

        table.Rows.Add(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"),
            DBNull.Value,
            DBNull.Value,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DBNull.Value);

        return table.CreateDataReader();
    }

    [Fact]
    public void A_result_set_becomes_a_queryable_duckdb_table()
    {
        using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");
        using var reader = BuildReader();

        var result = new DuckDbBulkLoader(connection).Load(reader, "crm_account");

        Assert.Equal(2, result.RowCount);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, revenue, createdon, primarycontactid, primarycontactid_entitytype
            FROM crm_account WHERE accountid = 'aaaaaaaa-0000-0000-0000-000000000001'
            """;

        using var rows = command.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal("Fourth Coffee", rows.GetString(0));
        Assert.Equal(1234.56m, rows.GetDecimal(1));
        Assert.Equal(new DateTime(2026, 8, 16, 10, 30, 0), rows.GetDateTime(2));
        Assert.Equal(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), rows.GetGuid(3));
        Assert.Equal("contact", rows.GetString(4));
    }

    [Fact]
    public void Nulls_survive_the_round_trip()
    {
        using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");
        using var reader = BuildReader();

        new DuckDbBulkLoader(connection).Load(reader, "crm_account");

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM crm_account
            WHERE name IS NULL AND revenue IS NULL
              AND primarycontactid IS NULL AND primarycontactid_entitytype IS NULL
            """;

        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void The_generated_table_can_be_joined_against_json()
    {
        // The actual goal of the project: CRM rows joined to local JSON.
        using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");
        using var reader = BuildReader();
        new DuckDbBulkLoader(connection).Load(reader, "crm_account");

        var json = Path.Combine(Path.GetTempPath(), $"dvduck-{Guid.NewGuid():N}.json");
        File.WriteAllText(json, """
            [{"account_id":"aaaaaaaa-0000-0000-0000-000000000001","level":"error"},
             {"account_id":"aaaaaaaa-0000-0000-0000-000000000001","level":"warn"},
             {"account_id":"aaaaaaaa-0000-0000-0000-000000000002","level":"error"}]
            """);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT coalesce(a.name, '(unnamed)') AS name, count(*) AS n
                FROM read_json_auto('{json}') j
                JOIN crm_account a ON a.accountid = CAST(j.account_id AS UUID)
                WHERE j.level = 'error'
                GROUP BY 1
                ORDER BY 1
                """;

            using var rows = command.ExecuteReader();

            // Both accounts have exactly one 'error' entry, and the second has
            // a null name, so ordering and the null must both be handled.
            Assert.True(rows.Read());
            Assert.Equal("(unnamed)", rows.GetString(0));
            Assert.Equal(1L, rows.GetInt64(1));

            Assert.True(rows.Read());
            Assert.Equal("Fourth Coffee", rows.GetString(0));
            Assert.Equal(1L, rows.GetInt64(1));

            Assert.False(rows.Read());
        }
        finally
        {
            File.Delete(json);
        }
    }

    [Fact]
    public void Table_names_are_quoted_so_reserved_words_work()
    {
        using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");
        using var reader = BuildReader();

        new DuckDbBulkLoader(connection).Load(reader, "order");

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM \"order\"";
        Assert.Equal(2L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void Cancellation_stops_the_load()
    {
        using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");
        using var reader = BuildReader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new DuckDbBulkLoader(connection).Load(reader, "crm_account",
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void The_create_statement_quotes_every_identifier()
    {
        var mapper = new DataverseSchemaMapper();
        var mapping = new TableMapping("crm_account", [
            .. mapper.MapColumn("accountid", typeof(Guid), 0),
            .. mapper.MapColumn("primarycontactid", typeof(EntityReference), 1),
        ]);

        var sql = mapping.ToCreateTableSql();

        Assert.Contains("CREATE OR REPLACE TABLE \"crm_account\"", sql);
        Assert.Contains("\"accountid\" UUID", sql);
        Assert.Contains("\"primarycontactid_entitytype\" VARCHAR", sql);
    }
}
