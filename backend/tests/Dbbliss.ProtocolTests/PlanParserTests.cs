using Dbbliss.Backend.Management;
using Dbbliss.Backend.Plans;

namespace Dbbliss.ProtocolTests;

/// <summary>The readers of the two engines' plan output, on plans captured from real servers (Fixtures/) and on small made-up ones.</summary>
public static class PlanParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static IEnumerable<PlanNode> Walk(PlanNode n) => new[] { n }.Concat(n.Children.SelectMany(Walk));

    private static void Near(double? actual, double expected, string what)
    {
        if (actual is null || Math.Abs(actual.Value - expected) > 1e-6) throw new TestFailure($"{what}: got {actual?.ToString() ?? "null"}, expected {expected}");
    }

    private static void Is<T>(T actual, T expected, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new TestFailure($"{what}: got {actual}, expected {expected}");
    }

    private static void Contains(string? text, string part, string what)
    {
        if (text is null || !text.Contains(part, StringComparison.Ordinal)) throw new TestFailure($"{what}: \"{text}\" lacks \"{part}\"");
    }

    private static async Task Fails(Action action, string contains, string what)
    {
        try
        {
            action();
        }
        catch (OperationFailedException ex) when (ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase))
        {
            await Task.CompletedTask;
            return;
        }
        catch (OperationFailedException ex)
        {
            throw new TestFailure($"{what}: failed, but \"{ex.Message}\" lacks \"{contains}\"");
        }
        throw new TestFailure($"{what}: expected a failure mentioning \"{contains}\"");
    }

    // Verifies: HLR-PLAN-7, LLR-PLAN-9
    public static void PostgresActualPlan()
    {
        var doc = PostgresPlanParser.Parse(Fixture("pg_actual.json"), "SELECT ...", PlanMode.Actual);
        var nodes = Walk(doc.Root).ToList();
        Is(nodes.Count, 7, "nodes");
        Is(string.Join(">", nodes.Select(n => n.Operator)), "Limit>Sort>Aggregate>Hash Join>Seq Scan>Hash>Seq Scan", "operators in order");
        Is(string.Join(",", nodes.Select(n => n.Id)), "0,1,2,3,4,5,6", "ids in preorder");
        var join = nodes[3];
        Contains(join.Detail, "Inner join", "join type");
        Contains(join.Detail, "hash cond: (b.a_id = a.id)", "join condition");
        Near(join.EstimatedRows, 4900, "join estimated rows");
        Near(join.Cost, 165.38, "join cost");
        Near(join.ActualRows, 4900, "join actual rows");
        Near(join.Loops, 1, "loops");
        Near(join.TimeMs, 3.002, "join time");
        var scan = nodes[4];
        Contains(scan.Detail, "pl_b", "relation");
        Contains(scan.Detail, "b", "alias");
        Contains(scan.Detail, "filter: (w > 100)", "filter");
        Near(nodes[0].ActualRows, 5, "limit rows");
        Contains(join.Buffers, "shared hit=45", "buffers");
        if (nodes[6].Buffers is not null && nodes[6].Buffers!.Contains("read=", StringComparison.Ordinal)) throw new TestFailure("zero counts are left out: " + nodes[6].Buffers);
        // own time = node minus children
        Near(nodes[2].SelfTimeMs, 4.901 - 3.002, "aggregate own time");
        Near(nodes[3].SelfTimeMs, 3.002 - (0.873 + 0.82), "join own time");
        Near(nodes[5].SelfTimeMs, 0.82 - 0.316, "hash own time");
        Near(nodes[6].SelfTimeMs, 0.316, "leaf own time");
        Near(nodes[2].SelfCost, 209.88 - 165.38, "aggregate own cost");
        Is(doc.HottestId, 2, "the aggregate has the greatest own time");
        Near(doc.Totals["planning_ms"], 2.229, "planning time");
        Near(doc.Totals["execution_ms"], 5.794, "execution time");
        Is(doc.Engine, "postgres", "engine");
        Is(doc.Mode, PlanMode.Actual, "mode");
        Contains(doc.Raw, "\"Node Type\"", "raw JSON");
        Is(doc.Statement, "SELECT ...", "statement");
    }

    // Verifies: HLR-PLAN-7
    public static void PostgresEstimatedPlan()
    {
        var doc = PostgresPlanParser.Parse(Fixture("pg_estimated.json"), "q", PlanMode.Estimated);
        var nodes = Walk(doc.Root).ToList();
        Is(nodes.Count, 4, "nodes");
        if (nodes.Any(n => n.ActualRows is not null || n.TimeMs is not null || n.SelfTimeMs is not null)) throw new TestFailure("an estimated plan has no actual figures");
        Near(nodes[0].SelfCost, 165.38 - (90.5 + 37.0), "join own cost");
        Is(doc.HottestId, 1, "the scan of pl_b has the greatest own cost");
        if (doc.Totals.ContainsKey("execution_ms")) throw new TestFailure("no execution time without running it");
    }

    // Verifies: LLR-PLAN-9
    public static async Task PostgresLoopsSpillsAndBadOutput()
    {
        const string looped = """
            [{"Plan": {"Node Type": "Nested Loop", "Join Type": "Inner", "Plan Rows": 10, "Total Cost": 100.0, "Actual Rows": 30, "Actual Loops": 1, "Actual Total Time": 5.0,
              "Plans": [
                {"Node Type": "Seq Scan", "Relation Name": "a", "Alias": "a", "Plan Rows": 10, "Total Cost": 10.0, "Actual Rows": 10, "Actual Loops": 1, "Actual Total Time": 1.0},
                {"Node Type": "Index Scan", "Index Name": "b_pkey", "Relation Name": "b", "Alias": "b", "Schema": "s", "Plan Rows": 1, "Total Cost": 8.0, "Actual Rows": 3, "Actual Loops": 10, "Actual Total Time": 0.5, "Index Cond": "(id = a.b_id)"},
                {"Node Type": "Sort", "Plan Rows": 5, "Total Cost": 20.0, "Actual Rows": 5, "Actual Loops": 1, "Actual Total Time": 1.0, "Sort Method": "external merge", "Sort Space Used": 1234, "Sort Space Type": "Disk"},
                {"Node Type": "Hash", "Plan Rows": 5, "Total Cost": 5.0, "Actual Rows": 5, "Actual Loops": 1, "Actual Total Time": 9.0, "Hash Batches": 4}
              ]}, "Planning Time": 0.1, "Execution Time": 5.0}]
            """;
        var doc = PostgresPlanParser.Parse(looped, "q", PlanMode.Actual);
        var nodes = Walk(doc.Root).ToList();
        Near(nodes[2].ActualRows, 30, "rows are per loop: 3 x 10");
        Near(nodes[2].TimeMs, 5.0, "time is per loop: 0.5 x 10");
        Contains(nodes[2].Detail, "s.b", "schema-qualified relation");
        Contains(nodes[2].Detail, "index b_pkey", "index");
        Contains(nodes[2].Detail, "index cond: (id = a.b_id)", "index condition");
        Contains(string.Join(";", nodes[3].Warnings), "disk", "a sort that went to disk");
        Contains(string.Join(";", nodes[3].Warnings), "1234 kB", "its size");
        Contains(string.Join(";", nodes[4].Warnings), "4 batches", "a hash in several batches");
        // children sum to more than the parent: own time never goes below 0
        Near(nodes[0].SelfTimeMs, 0, "own time is clamped at 0");
        Is(doc.HottestId, 4, "the hash node (9 ms) is the hottest");

        await Fails(() => PostgresPlanParser.Parse("not json", "q", PlanMode.Estimated), "plan", "text");
        await Fails(() => PostgresPlanParser.Parse("[]", "q", PlanMode.Estimated), "no plan", "empty array");
        await Fails(() => PostgresPlanParser.Parse("[{\"Other\": 1}]", "q", PlanMode.Estimated), "no plan", "no Plan member");
        await Fails(() => PostgresPlanParser.Parse("{\"Plan\": {}}", "q", PlanMode.Estimated), "plan", "an object, not the array EXPLAIN returns");
    }

    // Verifies: HLR-PLAN-7, LLR-PLAN-10
    public static void SqlServerEstimatedPlan()
    {
        var docs = SqlServerPlanParser.Parse(Fixture("ss_estimated.xml"), PlanMode.Estimated);
        Is(docs.Count, 1, "statements");
        var doc = docs[0];
        Contains(doc.Statement, "SELECT TOP 5 a.v", "statement text");
        var nodes = Walk(doc.Root).ToList();
        Is(nodes.Count, 6, "nodes");
        Is(string.Join(",", nodes.Select(n => n.Id)), "0,1,2,3,4,5", "NodeId");
        Is(string.Join(">", nodes.Select(n => n.Operator)), "Sort (TopN Sort)>Compute Scalar>Hash Match (Aggregate)>Hash Match (Inner Join)>Clustered Index Scan>Clustered Index Scan", "operators");
        Near(nodes[0].EstimatedRows, 5, "rows");
        Near(nodes[0].Cost, 0.2926, "cost");
        Near(nodes[5].EstimatedRows, 4900, "scan b rows");
        Contains(nodes[5].Detail, "[dbo].[pl_b]", "object");
        Contains(nodes[5].Detail, "as [b]", "alias");
        Contains(nodes[5].Detail, "index [PK__pl_b__", "index");
        Contains(nodes[5].Detail, "where [plt].[dbo].[pl_b].[w] as [b].[w]>(100)", "predicate");
        if (nodes.Any(n => n.ActualRows is not null || n.TimeMs is not null)) throw new TestFailure("an estimated plan has no actual figures");
        Near(nodes[3].SelfCost, 0.145037 - (0.0151116 + 0.0176709), "join own cost");
        Is(doc.HottestId, 3, "the hash join has the greatest own cost");
        Near(doc.Totals["statement_cost"], 0.2926, "statement cost");
        Near(doc.Totals["statement_est_rows"], 5, "statement rows");
        Near(doc.Totals["compile_ms"], 18, "compile time");
        if (doc.Totals.ContainsKey("dop")) throw new TestFailure("an estimated plan does not say how many threads ran");
        Contains(doc.Raw, "<ShowPlanXML", "raw XML");
    }

    // Verifies: HLR-PLAN-7, LLR-PLAN-10
    public static void SqlServerActualPlan()
    {
        var doc = SqlServerPlanParser.Parse(Fixture("ss_actual.xml"), PlanMode.Actual).Single();
        var nodes = Walk(doc.Root).ToList();
        Near(nodes[0].ActualRows, 5, "sort rows");
        Near(nodes[2].ActualRows, 2000, "aggregate rows");
        Near(nodes[3].ActualRows, 4900, "join rows");
        if (nodes[1].ActualRows is not null || nodes[1].TimeMs is not null) throw new TestFailure("Compute Scalar has no run-time figures of its own");
        Near(nodes[0].TimeMs, 5, "sort time");
        Near(nodes[2].TimeMs, 5, "aggregate time");
        Near(nodes[3].TimeMs, 2, "join time");
        // a node without figures is looked through: the sort's own time is its 5 ms minus the aggregate's 5 ms
        Near(nodes[0].SelfTimeMs, 0, "sort own time");
        if (nodes[1].SelfTimeMs is not null) throw new TestFailure("no own time for a node without time");
        Near(nodes[2].SelfTimeMs, 3, "aggregate own time");
        Near(nodes[3].SelfTimeMs, 2, "join own time");
        Is(doc.HottestId, 2, "the aggregate");
        Near(doc.Totals["memory_grant_kb"], 3440, "memory grant");
    }

    // Verifies: LLR-PLAN-10, HLR-PLAN-8
    public static async Task SqlServerThreadsWarningsAndBadOutput()
    {
        const string xml = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
              <StmtSimple StatementText="SET NOCOUNT ON" StatementId="1" StatementType="SET ON/OFF"/>
              <StmtSimple StatementText="SELECT 1" StatementId="2" StatementSubTreeCost="1.5" StatementEstRows="10">
                <QueryPlan DegreeOfParallelism="4" CompileTime="7">
                  <MissingIndexes><MissingIndexGroup Impact="95.5"><MissingIndex Database="[d]" Schema="[dbo]" Table="[t]">
                    <ColumnGroup Usage="EQUALITY"><Column Name="[w]"/></ColumnGroup><ColumnGroup Usage="INCLUDE"><Column Name="[a]"/><Column Name="[b]"/></ColumnGroup>
                  </MissingIndex></MissingIndexGroup></MissingIndexes>
                  <Warnings><PlanAffectingConvert ConvertIssue="Seek Plan" Expression="CONVERT_IMPLICIT(int,[t].[x],0)"/></Warnings>
                  <RelOp NodeId="0" PhysicalOp="Parallelism" LogicalOp="Gather Streams" EstimateRows="10" EstimatedTotalSubtreeCost="1.5">
                    <RunTimeInformation>
                      <RunTimeCountersPerThread Thread="0" ActualRows="0" ActualElapsedms="30"/>
                      <RunTimeCountersPerThread Thread="1" ActualRows="7" ActualElapsedms="20"/>
                      <RunTimeCountersPerThread Thread="2" ActualRows="5" ActualElapsedms="25"/>
                    </RunTimeInformation>
                    <Parallelism>
                      <RelOp NodeId="1" PhysicalOp="Table Scan" LogicalOp="Table Scan" EstimateRows="10" EstimatedTotalSubtreeCost="1.0">
                        <Warnings NoJoinPredicate="true" ColumnsWithNoStatistics="1"><SpillToTempDb SpillLevel="2"/></Warnings>
                        <RunTimeInformation><RunTimeCountersPerThread Thread="1" ActualRows="12" ActualElapsedms="22"/></RunTimeInformation>
                        <TableScan><Object Database="[d]" Schema="[dbo]" Table="[t]"/></TableScan>
                      </RelOp>
                    </Parallelism>
                  </RelOp>
                </QueryPlan>
              </StmtSimple>
              <StmtSimple StatementText="SELECT 2" StatementId="3"><QueryPlan><RelOp NodeId="0" PhysicalOp="Constant Scan" LogicalOp="Constant Scan" EstimateRows="1" EstimatedTotalSubtreeCost="0.1"/></QueryPlan></StmtSimple>
            </Statements></Batch></BatchSequence></ShowPlanXML>
            """;
        var docs = SqlServerPlanParser.Parse(xml, PlanMode.Actual);
        Is(docs.Count, 2, "one plan per statement that has one (the SET has none)");
        Is(docs[1].Statement, "SELECT 2", "second statement");
        var gather = docs[0].Root;
        Near(gather.ActualRows, 12, "rows are summed over threads: 0 + 7 + 5");
        Near(gather.TimeMs, 30, "time is the greatest of any thread");
        Contains(string.Join(";", gather.Children[0].Warnings), "no join predicate", "warning attribute");
        Contains(string.Join(";", gather.Children[0].Warnings), "columns with no statistics", "warning attribute");
        Contains(string.Join(";", gather.Children[0].Warnings), "spill to tempdb (level 2)", "spill");
        Contains(string.Join("\n", docs[0].Notes), "missing index", "missing index");
        Contains(string.Join("\n", docs[0].Notes), "95.5", "its impact");
        Contains(string.Join("\n", docs[0].Notes), "[dbo].[t] ([w]) include ([a], [b])", "its columns");
        Contains(string.Join("\n", docs[0].Notes), "Seek Plan", "a warning of the whole plan");
        Near(docs[0].Totals["dop"], 4, "dop");

        await Fails(() => SqlServerPlanParser.Parse("<nope", PlanMode.Estimated), "plan", "broken XML");
        await Fails(() => SqlServerPlanParser.Parse("<a/>", PlanMode.Estimated), "no plan", "XML that is no plan");
        await Fails(() => SqlServerPlanParser.Parse("<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements><StmtSimple StatementText=\"SET X\"/></Statements></Batch></BatchSequence></ShowPlanXML>", PlanMode.Estimated), "no plan", "statements without a plan");
    }

    // Verifies: HLR-PLAN-7
    public static void OwnFiguresAndTheHottestNode()
    {
        PlanNode N(int id, double? cost, double? time) => new() { Id = id, Operator = "n" + id, Cost = cost, TimeMs = time };
        // costs: the parent's is less than its children's (a model quirk): own cost 0, not negative
        var root = N(0, 10, null);
        var a = N(1, 8, null);
        var b = N(2, 6, null);
        root.Children.Add(a);
        root.Children.Add(b);
        a.Children.Add(N(3, 3, null));
        Is(PlanMath.Annotate(root), 2, "greatest own cost: 6 (node 2); node 1 has 5, root 0");
        Near(root.SelfCost, 0, "clamped");
        Near(a.SelfCost, 5, "8 - 3");
        if (root.SelfTimeMs is not null) throw new TestFailure("no times, no own time");
        // times win over costs when there are any
        var t = N(0, 100, 10);
        t.Children.Add(N(1, 1, 9.5));
        Is(PlanMath.Annotate(t), 1, "own time 9.5 beats own time 0.5, whatever the costs say");
        // a tie goes to the first node in plan order
        var tie = N(0, 4, null);
        tie.Children.Add(N(1, 2, null));
        tie.Children.Add(N(2, 2, null));
        Is(PlanMath.Annotate(tie), 1, "tie: the first (own cost 2: root has 0, node 1 has 2, node 2 has 2)");
        // nothing to compare
        Is(PlanMath.Annotate(new PlanNode { Id = 0, Operator = "x" }), null, "no figures, no hottest node");
    }
}
