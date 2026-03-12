using PlanViewer.Core.Models;

namespace SqlVersionControl.Services;

/// <summary>
/// Computes a 0-100 efficiency score for an execution plan.
/// Higher is better. Non-DBAs can use this as a quick health check.
/// </summary>
public static class PlanScorer
{
    public static (int Score, string Label, string Color) Score(
        ParsedPlan plan,
        IReadOnlyCollection<PlanWarning> warnings,
        IReadOnlyCollection<MissingIndex> missingIndexes)
    {
        var score = 100;
        var hasScans = false;

        // Walk all nodes and deduct for bad operators
        foreach (var batch in plan.Batches)
            foreach (var stmt in batch.Statements)
                if (stmt.RootNode != null)
                    score -= ScoreNode(stmt.RootNode, ref hasScans);

        // Deduct for warnings by type
        foreach (var w in warnings)
        {
            var type = w.WarningType.ToUpperInvariant();
            if (type.Contains("IMPLICIT CONVERSION"))
                score -= 10;
            else if (type.Contains("NO JOIN PREDICATE"))
                score -= 20;
            else if (type.Contains("SPILL"))
                score -= 10;
        }

        // Deduct for missing indexes
        score -= missingIndexes.Count * 5;

        // Bonus: all seeks, no scans
        if (!hasScans && score > 0)
            score += 5;

        score = Math.Clamp(score, 0, 100);

        var (label, color) = score switch
        {
            >= 80 => ("Healthy", "#27ae60"),
            >= 50 => ("Needs attention", "#f39c12"),
            _ => ("Poor \u2014 review recommended", "#e74c3c")
        };

        return (score, label, color);
    }

    private static int ScoreNode(PlanNode node, ref bool hasScans)
    {
        var deduction = 0;
        var op = node.PhysicalOp.ToUpperInvariant();

        if (op.Contains("TABLE SCAN"))
        {
            deduction += 15;
            hasScans = true;
        }
        else if (op.Contains("CLUSTERED INDEX SCAN"))
        {
            deduction += 10;
            hasScans = true;
        }
        else if (op.Contains("INDEX SCAN"))
        {
            hasScans = true;
        }

        if (op.Contains("KEY LOOKUP") || op.Contains("RID LOOKUP"))
            deduction += 5;

        foreach (var child in node.Children)
            deduction += ScoreNode(child, ref hasScans);

        return deduction;
    }
}
