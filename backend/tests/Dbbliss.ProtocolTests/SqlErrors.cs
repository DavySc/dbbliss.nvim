using System.Reflection;
using Microsoft.Data.SqlClient;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// A <see cref="SqlException"/> with a chosen error number, for code that tells server errors apart by number.
/// The driver has no public way to make one, so this goes through its internals; if a driver update changes them
/// the tests that use it fail at once with a clear message rather than pass.
/// </summary>
public static class SqlErrors
{
    public static SqlException Make(int number, string message)
    {
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        var errorCtor = typeof(SqlError).GetConstructors(Any).OrderByDescending(c => c.GetParameters().Length).First();
        var args = errorCtor.GetParameters().Select(p => p.Name switch
        {
            "infoNumber" => number,
            "errorState" => (byte)1,
            "errorClass" => (byte)16,
            "server" => "test",
            "errorMessage" => message,
            "procedure" => "",
            "lineNumber" => 0,
            _ => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null,
        }).ToArray();
        var error = (SqlError)errorCtor.Invoke(args);
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), true)!;
        typeof(SqlErrorCollection).GetMethod("Add", Any)!.Invoke(collection, [error]);
        var factory = typeof(SqlException).GetMethod("CreateException", BindingFlags.NonPublic | BindingFlags.Static, null,
            [typeof(SqlErrorCollection), typeof(string)], null)
            ?? throw new TestFailure("SqlException.CreateException(SqlErrorCollection, string) not found: the driver changed");
        return (SqlException)factory.Invoke(null, [collection, "7.0.0"])!;
    }
}
