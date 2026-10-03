using System.Data.Odbc;
using System.Globalization;

namespace Dbbliss.Backend.Engines;

/// <summary>
/// DB2 for i via System.Data.Odbc and the IBM i Access ODBC driver.
/// Cancel = OdbcCommand.Cancel() → SQLCancel. System.Data.Odbc has no real async I/O, so every
/// driver call runs on a dedicated thread and cancel arrives from another thread.
/// </summary>
public sealed class Db2iEngine : IEngine
{
    public string Name => "db2i";

    public async Task<IEngineSession> OpenAsync(ConnectionSpec spec, IMessageSink messages, CancellationToken ct)
    {
        var cs = spec.ConnectionString;
        if (spec.Password is not null)
        {
            cs = cs.TrimEnd(';') + ";PWD={" + spec.Password.Replace("}", "}}", StringComparison.Ordinal) + "};";
        }
        var conn = new OdbcConnection(cs);
        conn.InfoMessage += (_, e) =>
        {
            foreach (OdbcError err in e.Errors)
            {
                messages.Message("info", err.Message, err.NativeError);
            }
        };
        try
        {
            await Task.Run(conn.Open, ct);
            var job = await Task.Run(() => QueryJobName(conn, messages), ct);
            return new Session(conn, job);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static string QueryJobName(OdbcConnection conn, IMessageSink messages)
    {
        // QSYS2.JOB_NAME is a built-in global variable (IBM i 7.2+): "number/user/name" of the QZDASOINIT job.
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT QSYS2.JOB_NAME FROM SYSIBM.SYSDUMMY1";
            return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "unknown";
        }
        catch (OdbcException ex)
        {
            messages.Message("warning", $"Could not determine the server job name: {ex.Message}");
            return "unknown";
        }
    }

    public ErrorInfo DescribeError(Exception ex)
    {
        if (ex is OdbcException odbc && odbc.Errors.Count > 0)
        {
            var e = odbc.Errors[0];
            return new ErrorInfo(odbc.Message, Code: e.NativeError.ToString(CultureInfo.InvariantCulture), SqlState: e.SQLState);
        }
        return new ErrorInfo(ex.Message);
    }

    private sealed class Session(OdbcConnection conn, string jobName) : IEngineSession
    {
        private OdbcTransaction? _tx;

        public string ServerSessionId => jobName;

        public Task<ExecuteSummary> ExecuteAsync(string sql, IResultSink sink, QueryControl control) =>
            Task.Factory.StartNew(() =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 0;
                cmd.Transaction = _tx;
                control.SetProtocolCancel(cmd.Cancel);
                try
                {
                    control.Token.ThrowIfCancellationRequested();
                    using var reader = cmd.ExecuteReader();
                    AdoStreaming.Stream(reader, sink);
                    reader.Close();
                    return new ExecuteSummary(reader.RecordsAffected);
                }
                finally
                {
                    control.ClearProtocolCancel();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        public Task BeginTransactionAsync(CancellationToken ct) => Task.Run(() =>
        {
            if (_tx is not null) throw new InvalidOperationException("A transaction is already open.");
            _tx = conn.BeginTransaction();
        }, ct);

        public Task CommitAsync(CancellationToken ct) => Task.Run(() =>
        {
            var tx = _tx ?? throw new InvalidOperationException("No open transaction.");
            tx.Commit();
            tx.Dispose();
            _tx = null;
        }, ct);

        public Task RollbackAsync(CancellationToken ct) => Task.Run(() =>
        {
            if (_tx is null) return;
            _tx.Rollback();
            _tx.Dispose();
            _tx = null;
        }, ct);

        // Unverified: no reliable server-side probe yet for "this job's commitment definition is
        // in a failed state". Phase 0 reports local bookkeeping as Unknown so nobody trusts it.
        public Task<TransactionState> GetTransactionStateAsync(CancellationToken ct) =>
            Task.FromResult(_tx is null ? TransactionState.None : TransactionState.Unknown);

        public ValueTask DisposeAsync()
        {
            _tx?.Dispose();
            conn.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
