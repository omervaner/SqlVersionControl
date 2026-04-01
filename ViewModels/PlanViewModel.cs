using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlanViewer.Core.Models;
using SqlVersionControl.Services;

namespace SqlVersionControl.ViewModels;

public partial class PlanViewModel : ViewModelBase
{
    private readonly DatabaseService _db;
    private readonly MainWindowViewModel _mainVm;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _planError;

    [ObservableProperty]
    private string _sqlText = "";

    [ObservableProperty]
    private string _currentObjectName = "";

    [ObservableProperty]
    private PlanNodeViewModel? _selectedNode;

    private ParsedPlan? _currentPlan;
    private string? _rawPlanXml;
    private Dictionary<string, (int StartChar, int EndChar)> _statementOffsets = new();

    [ObservableProperty]
    private int _efficiencyScore;

    [ObservableProperty]
    private string _efficiencyLabel = "";

    [ObservableProperty]
    private string _efficiencyColor = "#95a5a6";

    public ObservableCollection<PlanNodeViewModel> OperatorNodes { get; } = new();
    public ObservableCollection<CostSegment> CostSegments { get; } = new();
    public ObservableCollection<WarningDisplayItem> Warnings { get; } = new();
    public ObservableCollection<MissingIndex> MissingIndexes { get; } = new();

    public bool HasPlan => _currentPlan != null;
    public bool HasWarnings => Warnings.Count > 0;
    public bool HasMissingIndexes => MissingIndexes.Count > 0;
    public bool HasPlanFindings => HasWarnings || HasMissingIndexes;

    /// <summary>
    /// Fired after plan is generated (or cleared) so the View can rebuild the cost bar.
    /// </summary>
    public event Action? PlanChanged;

    /// <summary>
    /// Fired when the SQL highlight range changes (node selected/deselected).
    /// </summary>
    public event Action? HighlightChanged;

    public int HighlightStart { get; private set; } = -1;
    public int HighlightEnd { get; private set; } = -1;

    public PlanViewModel(DatabaseService db, MainWindowViewModel mainVm)
    {
        _db = db;
        _mainVm = mainVm;
    }

    /// <summary>
    /// Lightweight constructor for embedded use (no MainWindowViewModel needed).
    /// </summary>
    public PlanViewModel(DatabaseService db)
    {
        _db = db;
        _mainVm = null!;
    }

    /// <summary>
    /// Loads a pre-generated plan for display. Used by the embedded editor plan panel.
    /// </summary>
    public void LoadFromQuery(string sql, string planXml, string label)
    {
        PlanError = null;
        CurrentObjectName = label;
        SqlText = sql;
        _rawPlanXml = planXml;

        var (plan, error) = DatabaseService.ParseAndAnalyzePlan(planXml);
        if (plan == null)
        {
            PlanError = $"Failed to parse plan: {error}";
            ClearPlan();
            NotifyPlanState();
            PlanChanged?.Invoke();
            return;
        }

        _currentPlan = plan;
        PopulateFromPlan(plan);
        NotifyPlanState();
        PlanChanged?.Invoke();
    }

    partial void OnSelectedNodeChanged(PlanNodeViewModel? value)
    {
        if (value?.Statement == null)
        {
            HighlightStart = -1;
            HighlightEnd = -1;
            HighlightChanged?.Invoke();
            return;
        }

        var stmtText = value.Statement.StatementText;

        // Try XML offsets first (precise byte-offset based)
        if (_statementOffsets.TryGetValue(stmtText, out var offsets) &&
            offsets.StartChar >= 0 && offsets.EndChar > offsets.StartChar &&
            offsets.EndChar <= SqlText.Length)
        {
            HighlightStart = offsets.StartChar;
            HighlightEnd = offsets.EndChar;
        }
        else
        {
            // Fallback: find StatementText substring in SqlText
            var trimmed = stmtText.Trim();
            var idx = SqlText.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                HighlightStart = idx;
                HighlightEnd = idx + trimmed.Length;
            }
            else
            {
                HighlightStart = -1;
                HighlightEnd = -1;
            }
        }

        HighlightChanged?.Invoke();
    }

    [RelayCommand]
    private async Task GeneratePlanAsync()
    {
        var database = _mainVm.SelectedDatabase;
        var obj = _mainVm.SelectedObject;

        if (string.IsNullOrEmpty(database) || obj == null || obj.IsSectionHeader)
        {
            PlanError = "Select an object in Version History first";
            return;
        }

        IsLoading = true;
        PlanError = null;
        CurrentObjectName = $"{obj.SchemaName}.{obj.ObjectName}";

        try
        {
            // Get the SQL definition for display
            var versions = await _db.GetObjectHistoryAsync(database, obj.SchemaName, obj.ObjectName);
            SqlText = versions.Count > 0 ? versions[0].Definition : "";

            // Generate the execution plan
            var xml = await _db.GetEstimatedPlanAsync(database, obj.SchemaName, obj.ObjectName);
            if (string.IsNullOrEmpty(xml))
            {
                PlanError = "No execution plan returned — proc may require parameters with no cached plan available.";
                ClearPlan();
                return;
            }

            _rawPlanXml = xml;
            var (plan, error) = DatabaseService.ParseAndAnalyzePlan(xml);
            if (plan == null)
            {
                PlanError = $"Failed to parse plan: {error}";
                ClearPlan();
                return;
            }

            _currentPlan = plan;
            PopulateFromPlan(plan);
        }
        catch (Exception ex)
        {
            PlanError = $"Error: {ex.Message}";
            ClearPlan();
        }
        finally
        {
            IsLoading = false;
            NotifyPlanState();
            PlanChanged?.Invoke();
        }
    }

    [RelayCommand]
    private async Task CopyCreateIndexAsync(MissingIndex? mi)
    {
        if (mi == null || string.IsNullOrEmpty(mi.CreateStatement)) return;

        var clipboard = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow?.Clipboard
            : null;

        if (clipboard != null)
            await clipboard.SetTextAsync(mi.CreateStatement);
    }

    private void ClearPlan()
    {
        _currentPlan = null;
        _rawPlanXml = null;
        OperatorNodes.Clear();
        CostSegments.Clear();
        Warnings.Clear();
        MissingIndexes.Clear();
    }

    private void NotifyPlanState()
    {
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(HasMissingIndexes));
        OnPropertyChanged(nameof(HasPlanFindings));
    }

    private const int MaxCostSegments = 5;

    private void PopulateFromPlan(ParsedPlan plan)
    {
        OperatorNodes.Clear();
        CostSegments.Clear();
        Warnings.Clear();
        MissingIndexes.Clear();

        // Extract statement offsets from raw XML for code-to-plan linking
        _statementOffsets = !string.IsNullOrEmpty(_rawPlanXml)
            ? PlanXmlHelper.ExtractStatementOffsets(_rawPlanXml)
            : new();

        // First pass: flatten trees, collect warnings & missing indexes
        foreach (var batch in plan.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                if (stmt.RootNode == null) continue;

                FlattenTree(stmt.RootNode, 0, stmt);
                CollectWarnings(stmt.RootNode);

                foreach (var mi in stmt.MissingIndexes)
                    MissingIndexes.Add(mi);
            }
        }

        // Statement-level plan warnings
        foreach (var batch in plan.Batches)
            foreach (var stmt in batch.Statements)
                foreach (var w in stmt.PlanWarnings)
                    Warnings.Add(new WarningDisplayItem(w, null));

        // Build cost bar globally across ALL statements — top 5 + Other
        var allCostNodes = new List<(PlanNode Node, double Cost)>();
        foreach (var batch in plan.Batches)
            foreach (var stmt in batch.Statements)
                if (stmt.RootNode != null)
                    CollectCostNodes(stmt.RootNode, allCostNodes);

        BuildCostSegments(allCostNodes);

        // Compute efficiency score
        var allWarnings = Warnings.Select(w => w.Warning).ToList();
        var (score, label, color) = PlanScorer.Score(plan, allWarnings, MissingIndexes);
        EfficiencyScore = score;
        EfficiencyLabel = label;
        EfficiencyColor = color;
    }

    private void FlattenTree(PlanNode node, int level, PlanStatement? statement)
    {
        OperatorNodes.Add(new PlanNodeViewModel(node, level, statement));
        foreach (var child in node.Children)
            FlattenTree(child, level + 1, statement);
    }

    private void BuildCostSegments(List<(PlanNode Node, double Cost)> allNodes)
    {
        var totalCost = allNodes.Sum(n => n.Cost);
        if (totalCost <= 0) return;

        // Compute real percentages from operator costs
        var withPct = allNodes
            .Select(n => (n.Node, Pct: n.Cost / totalCost * 100.0))
            .OrderByDescending(n => n.Pct)
            .ToList();

        var top = withPct.Take(MaxCostSegments).ToList();
        var rest = withPct.Skip(MaxCostSegments).ToList();

        foreach (var (node, pct) in top)
        {
            var rawOp = node.PhysicalOp;
            if (!string.IsNullOrEmpty(node.LogicalOp) && node.LogicalOp != node.PhysicalOp)
                rawOp += $" ({node.LogicalOp})";

            CostSegments.Add(new CostSegment
            {
                Label = $"{PlanTranslator.Translate(node)} \u2014 {pct:F0}%",
                RawLabel = $"{rawOp} \u2014 {pct:F0}%",
                Percentage = pct,
                Color = GetOperatorColor(node.PhysicalOp),
                NodeId = node.NodeId
            });
        }

        // Lump the rest into a single grey "Other" segment
        var otherPct = rest.Sum(n => n.Pct);
        if (otherPct > 0.5)
        {
            CostSegments.Add(new CostSegment
            {
                Label = $"Other ({rest.Count} operators) — {otherPct:F0}%",
                Percentage = Math.Max(otherPct, 1),
                Color = "#666666",
                NodeId = -1
            });
        }
    }

    /// <summary>
    /// Collect leaf nodes only — they represent actual work (seeks, scans, sorts, lookups).
    /// Uses EstimatedOperatorCost (the node's own cost, not subtree) so percentages sum to ~100%.
    /// </summary>
    private static void CollectCostNodes(PlanNode node, List<(PlanNode Node, double Cost)> list)
    {
        if (node.Children.Count == 0 && node.EstimatedOperatorCost > 0)
            list.Add((node, node.EstimatedOperatorCost));
        foreach (var child in node.Children)
            CollectCostNodes(child, list);
    }

    private void CollectWarnings(PlanNode node)
    {
        foreach (var w in node.Warnings)
            Warnings.Add(new WarningDisplayItem(w, node.PhysicalOp));
        foreach (var child in node.Children)
            CollectWarnings(child);
    }

    public static string GetOperatorColor(string physicalOp)
    {
        var op = physicalOp.ToUpperInvariant();
        if (op.Contains("INDEX SEEK")) return "#27ae60";
        if (op.Contains("TABLE SCAN") || op.Contains("CLUSTERED INDEX SCAN")) return "#e74c3c";
        if (op.Contains("INDEX SCAN")) return "#e67e22";
        if (op.Contains("SORT") || op.Contains("HASH")) return "#f39c12";
        if (op.Contains("NESTED LOOPS") || op.Contains("MERGE JOIN")) return "#3498db";
        if (op.Contains("KEY LOOKUP") || op.Contains("RID LOOKUP")) return "#e74c3c";
        if (op.Contains("PARALLELISM")) return "#9b59b6";
        return "#95a5a6";
    }
}

public class PlanNodeViewModel
{
    public PlanNode Node { get; }
    public int Level { get; }
    public PlanStatement? Statement { get; }

    public string PhysicalOp => Node.PhysicalOp;
    public string LogicalOp => Node.LogicalOp;
    public int CostPercent => Node.CostPercent;
    public double EstimateRows => Node.EstimateRows;
    public string? TableName => Node.ObjectName;
    public string? IndexName => Node.IndexName;
    public string Color => PlanViewModel.GetOperatorColor(Node.PhysicalOp);
    public bool HasWarnings => Node.HasWarnings;

    public string DisplayLabel => PlanTranslator.Translate(Node);

    public string RawLabel
    {
        get
        {
            var label = PhysicalOp;
            if (!string.IsNullOrEmpty(LogicalOp) && LogicalOp != PhysicalOp)
                label += $" ({LogicalOp})";
            return label;
        }
    }

    public string ObjectInfo
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(TableName)) parts.Add(TableName);
            if (!string.IsNullOrEmpty(IndexName)) parts.Add(IndexName);
            return parts.Count > 0 ? string.Join(" · ", parts) : "";
        }
    }

    public string RowsDisplay => EstimateRows switch
    {
        >= 1_000_000 => $"{EstimateRows / 1_000_000:F1}M rows",
        >= 1_000 => $"{EstimateRows / 1_000:F1}K rows",
        _ => $"{EstimateRows:F0} rows"
    };

    public double IndentWidth => Level * 20;
    public Avalonia.Thickness IndentBorderThickness => Level > 0
        ? new Avalonia.Thickness(1, 0, 0, 0)
        : new Avalonia.Thickness(0);

    public PlanNodeViewModel(PlanNode node, int level, PlanStatement? statement = null)
    {
        Node = node;
        Level = level;
        Statement = statement;
    }
}

public class CostSegment
{
    public string Label { get; set; } = "";
    public string RawLabel { get; set; } = "";
    public double Percentage { get; set; }
    public string Color { get; set; } = "#95a5a6";
    public int NodeId { get; set; }
}

public class WarningDisplayItem
{
    public PlanWarning Warning { get; }
    public string Message => Warning.Message;
    public string WarningType => Warning.WarningType;
    public PlanWarningSeverity Severity => Warning.Severity;

    /// <summary>Source operator name, or null for statement-level warnings.</summary>
    public string? OperatorName { get; }

    public string OperatorLabel => OperatorName != null ? $"[{OperatorName}]" : "[Statement]";
    public bool HasOperator => true;

    public string SeverityIcon => Severity switch
    {
        PlanWarningSeverity.Critical => "\u26d4",  // ⛔
        PlanWarningSeverity.Warning => "\u26a0",   // ⚠
        _ => "\u2139"                               // ℹ
    };

    public Avalonia.Media.ISolidColorBrush SeverityBrush => Severity switch
    {
        PlanWarningSeverity.Critical => Avalonia.Media.SolidColorBrush.Parse("#e74c3c"),
        PlanWarningSeverity.Warning => Avalonia.Media.SolidColorBrush.Parse("#f39c12"),
        _ => Avalonia.Media.SolidColorBrush.Parse("#888888")
    };

    public WarningDisplayItem(PlanWarning warning, string? operatorName)
    {
        Warning = warning;
        OperatorName = operatorName;
    }
}

public class ColumnListConverter : IValueConverter
{
    public static readonly ColumnListConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IEnumerable<string> list)
            return string.Join(", ", list);
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
