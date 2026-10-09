using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// How driver values reach JSON. The rule is that nothing is rounded or reinterpreted on the way:
/// whatever a Lua double cannot hold exactly travels as a string.
/// </summary>
public static class ValueCases
{
    public static void Run()
    {
        // Small integers stay numbers; 64-bit values past 2^53 become strings.
        Is("5", (byte)5);
        Is("-5", (sbyte)-5);
        Is("32767", (short)32767);
        Is("65535", (ushort)65535);
        Is("2147483647", int.MaxValue);
        Is("4294967295", uint.MaxValue);
        Is("9007199254740992", 1L << 53);
        Is("-9007199254740992", -(1L << 53));
        Is("\"9007199254740993\"", (1L << 53) + 1);
        Is("\"-9007199254740993\"", -(1L << 53) - 1);
        Is("\"9223372036854775807\"", long.MaxValue);
        Is("\"-9223372036854775808\"", long.MinValue);
        Is("\"18446744073709551615\"", ulong.MaxValue);

        // Decimals keep every digit, culture-independent.
        Is("\"79228162514264337593543950335\"", decimal.MaxValue);
        Is("\"0.10\"", 0.10m);
        Is("\"-1234.5000\"", -1234.5000m);

        // Floating point: finite doubles are numbers, the rest strings; floats round-trip ("R").
        Is("1.5", 1.5d);
        Is("\"NaN\"", double.NaN);
        Is("\"Infinity\"", double.PositiveInfinity);
        Is("\"-Infinity\"", double.NegativeInfinity);
        Is("\"0.1\"", 0.1f);
        Is("\"3.4028235E+38\"", float.MaxValue);

        // Text, booleans, NULL.
        Is("\"\"", "");
        Is("\"héllo \\u2603\"", "héllo \u2603");
        Is("true", true);
        Is("false", false);
        if (ValueConverter.Convert(null) is not null || ValueConverter.Convert(DBNull.Value) is not null)
        {
            throw new TestFailure("NULL and DBNull must become JSON null");
        }

        // Dates and times are text in a fixed format; fractions only when present.
        Is("\"2026-10-09 12:34:56\"", new DateTime(2026, 10, 9, 12, 34, 56));
        Is("\"2026-10-09 12:34:56.1234567\"", new DateTime(2026, 10, 9, 12, 34, 56).AddTicks(1234567));
        Is("\"2026-10-09 12:34:56+02:00\"", new DateTimeOffset(2026, 10, 9, 12, 34, 56, TimeSpan.FromHours(2)));
        Is("\"2026-10-09 12:34:56-05:30\"", new DateTimeOffset(2026, 10, 9, 12, 34, 56, TimeSpan.FromMinutes(-330)));
        Is("\"2026-10-09\"", new DateOnly(2026, 10, 9));
        Is("\"01:02:03.5\"", new TimeOnly(1, 2, 3, 500));
        Is("\"1.02:03:04\"", new TimeSpan(1, 2, 3, 4));

        // Identifiers and binary.
        Is("\"00000000-0000-0000-0000-000000000001\"", new Guid("00000000-0000-0000-0000-000000000001"));
        Is("\"0x\"", Array.Empty<byte>());
        Is("\"0x00FF10\"", new byte[] { 0, 255, 16 });

        // Anything else formattable is formatted without the culture; anything unknown becomes its text.
        Is("\"123456789012345678901234567890\"", System.Numerics.BigInteger.Parse("123456789012345678901234567890"));
        Is("\"10.0.0.1\"", System.Net.IPAddress.Parse("10.0.0.1"));
        Is("\"http://example.test/a\"", new Uri("http://example.test/a")); // not formattable: its text

        // A whole row from a driver: NULL and DBNull become JSON null, the rest is converted.
        var table = new System.Data.DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("big", typeof(long));
        table.Rows.Add(1, "x", (1L << 53) + 1);
        table.Rows.Add(2, DBNull.Value, DBNull.Value);
        using (var reader = table.CreateDataReader())
        {
            var columns = ValueConverter.Columns(reader);
            if (string.Join(",", columns.Select(c => c.Name)) != "id,name,big") throw new TestFailure("column names");
            reader.Read();
            if (ValueConverter.ReadRow(reader).ToJsonString() != "[1,\"x\",\"9007199254740993\"]") throw new TestFailure("row 1: " + ValueConverter.ReadRow(reader).ToJsonString());
            reader.Read();
            if (ValueConverter.ReadRow(reader).ToJsonString() != "[2,null,null]") throw new TestFailure("row 2: " + ValueConverter.ReadRow(reader).ToJsonString());
        }

        // The result does not depend on the thread's culture (a comma-decimal one must not leak).
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Is("\"1234.5\"", 1234.5m);
            Is("2.5", 2.5d);
            Is("\"2026-10-09 01:02:03\"", new DateTime(2026, 10, 9, 1, 2, 3));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }

    private static void Is(string expectedJson, object value)
    {
        var actual = ValueConverter.Convert(value)?.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var expected = JsonNode.Parse(expectedJson)!.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        if (actual != expected) throw new TestFailure($"{value.GetType().Name} {value}: expected {expected}, got {actual}");
    }
}
