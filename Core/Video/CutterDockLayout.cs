using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;

namespace CortexFX.Core.Video;

public enum CutterPanelId
{
    Project,
    Program,
    Timeline
}

public enum DockDropZone
{
    None,
    Center,
    Left,
    Right,
    Top,
    Bottom
}

public enum CutterDockPreset
{
    Editing,
    PremiereClassic,
    VerticalMedia
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CutterDockLeaf), "leaf")]
[JsonDerivedType(typeof(CutterDockSplit), "split")]
public abstract class CutterDockNode
{
    public abstract CutterDockNode Clone();
}

public sealed class CutterDockLeaf : CutterDockNode
{
    public CutterDockLeaf() { }

    public CutterDockLeaf(CutterPanelId panel)
    {
        Panel = panel;
    }

    public CutterPanelId Panel { get; set; }

    public override CutterDockNode Clone() => new CutterDockLeaf(Panel);
}

public sealed class CutterDockSplit : CutterDockNode
{
    public CutterDockSplit() { }

    public CutterDockSplit(Orientation orientation, CutterDockNode first, CutterDockNode second, double ratio = 0.5)
    {
        Orientation = orientation;
        First = first;
        Second = second;
        Ratio = Math.Clamp(ratio, 0.15, 0.85);
    }

    public Orientation Orientation { get; set; }
    public CutterDockNode First { get; set; } = new CutterDockLeaf(CutterPanelId.Project);
    public CutterDockNode Second { get; set; } = new CutterDockLeaf(CutterPanelId.Program);
    public double Ratio { get; set; } = 0.5;

    public override CutterDockNode Clone() =>
        new CutterDockSplit(Orientation, First.Clone(), Second.Clone(), Ratio);
}

public static class CutterDockLayoutManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string LayoutFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CortexFX",
        "cutter_layout.json");

    public static CutterDockNode CreatePreset(CutterDockPreset preset) => preset switch
    {
        CutterDockPreset.PremiereClassic => new CutterDockSplit(
            Orientation.Horizontal,
            new CutterDockLeaf(CutterPanelId.Project),
            new CutterDockSplit(
                Orientation.Vertical,
                new CutterDockLeaf(CutterPanelId.Program),
                new CutterDockLeaf(CutterPanelId.Timeline),
                0.56),
            0.24),

        CutterDockPreset.VerticalMedia => new CutterDockSplit(
            Orientation.Horizontal,
            new CutterDockLeaf(CutterPanelId.Program),
            new CutterDockSplit(
                Orientation.Vertical,
                new CutterDockLeaf(CutterPanelId.Project),
                new CutterDockLeaf(CutterPanelId.Timeline),
                0.38),
            0.35),

        _ => new CutterDockSplit(
            Orientation.Vertical,
            new CutterDockSplit(
                Orientation.Horizontal,
                new CutterDockLeaf(CutterPanelId.Project),
                new CutterDockLeaf(CutterPanelId.Program),
                0.25),
            new CutterDockLeaf(CutterPanelId.Timeline),
            0.57)
    };

    public static CutterDockNode DockRelative(
        CutterDockNode root, CutterPanelId source, CutterPanelId target, DockDropZone zone)
    {
        if (source == target || zone == DockDropZone.None)
            return root;

        if (zone == DockDropZone.Center)
            return SwapPanels(root.Clone(), source, target);

        CutterDockNode? stripped = RemovePanel(root.Clone(), source);
        if (stripped == null)
            return root;

        Orientation orientation = zone is DockDropZone.Left or DockDropZone.Right
            ? Orientation.Horizontal
            : Orientation.Vertical;
        bool sourceFirst = zone is DockDropZone.Left or DockDropZone.Top;
        double defaultRatio = zone switch
        {
            DockDropZone.Left => 0.32,
            DockDropZone.Right => 0.68,
            DockDropZone.Top => 0.42,
            DockDropZone.Bottom => 0.58,
            _ => 0.5
        };

        CutterDockNode updated = ReplaceLeaf(stripped, target, targetLeaf =>
            sourceFirst
                ? new CutterDockSplit(orientation, new CutterDockLeaf(source), targetLeaf, defaultRatio)
                : new CutterDockSplit(orientation, targetLeaf, new CutterDockLeaf(source), defaultRatio));

        return IsValid(updated) ? updated : root;
    }

    public static CutterDockNode DockOuterEdge(
        CutterDockNode root, CutterPanelId source, DockDropZone edgeZone)
    {
        if (edgeZone is DockDropZone.None or DockDropZone.Center)
            return root;

        CutterDockNode? stripped = RemovePanel(root.Clone(), source);
        if (stripped == null)
            return root;

        Orientation orientation = edgeZone is DockDropZone.Left or DockDropZone.Right
            ? Orientation.Horizontal
            : Orientation.Vertical;
        bool sourceFirst = edgeZone is DockDropZone.Left or DockDropZone.Top;
        double ratio = source switch
        {
            CutterPanelId.Timeline when !sourceFirst => 0.57,
            CutterPanelId.Timeline when sourceFirst => 0.43,
            CutterPanelId.Project when sourceFirst => 0.25,
            CutterPanelId.Project when !sourceFirst => 0.75,
            _ => sourceFirst ? 0.35 : 0.65
        };

        CutterDockNode updated = sourceFirst
            ? new CutterDockSplit(orientation, new CutterDockLeaf(source), stripped, ratio)
            : new CutterDockSplit(orientation, stripped, new CutterDockLeaf(source), ratio);

        return IsValid(updated) ? updated : root;
    }

    public static bool IsValid(CutterDockNode? node)
    {
        if (node == null) return false;
        var seen = new HashSet<CutterPanelId>();
        int count = 0;
        Collect(node);
        return count == 3 &&
               seen.Contains(CutterPanelId.Project) &&
               seen.Contains(CutterPanelId.Program) &&
               seen.Contains(CutterPanelId.Timeline);

        void Collect(CutterDockNode n)
        {
            if (n is CutterDockLeaf leaf)
            {
                seen.Add(leaf.Panel);
                count++;
            }
            else if (n is CutterDockSplit split)
            {
                Collect(split.First);
                Collect(split.Second);
            }
        }
    }

    public static CutterDockNode LoadOrDefault()
    {
        try
        {
            if (File.Exists(LayoutFilePath))
            {
                string json = File.ReadAllText(LayoutFilePath);
                CutterDockNode? loaded = JsonSerializer.Deserialize<CutterDockNode>(json, JsonOptions);
                if (IsValid(loaded))
                    return loaded!;
            }
        }
        catch { }

        return CreatePreset(CutterDockPreset.Editing);
    }

    public static void Save(CutterDockNode root)
    {
        if (!IsValid(root)) return;
        try
        {
            string? dir = Path.GetDirectoryName(LayoutFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(root, JsonOptions);
            File.WriteAllText(LayoutFilePath, json);
        }
        catch { }
    }

    private static CutterDockNode SwapPanels(CutterDockNode node, CutterPanelId a, CutterPanelId b)
    {
        if (node is CutterDockLeaf leaf)
        {
            if (leaf.Panel == a) leaf.Panel = b;
            else if (leaf.Panel == b) leaf.Panel = a;
            return leaf;
        }
        if (node is CutterDockSplit split)
        {
            split.First = SwapPanels(split.First, a, b);
            split.Second = SwapPanels(split.Second, a, b);
        }
        return node;
    }

    private static CutterDockNode? RemovePanel(CutterDockNode node, CutterPanelId panel)
    {
        if (node is CutterDockLeaf leaf)
            return leaf.Panel == panel ? null : leaf;

        if (node is CutterDockSplit split)
        {
            CutterDockNode? first = RemovePanel(split.First, panel);
            CutterDockNode? second = RemovePanel(split.Second, panel);
            if (first == null) return second;
            if (second == null) return first;
            split.First = first;
            split.Second = second;
            return split;
        }
        return node;
    }

    private static CutterDockNode ReplaceLeaf(
        CutterDockNode node, CutterPanelId target, Func<CutterDockLeaf, CutterDockNode> replacer)
    {
        if (node is CutterDockLeaf leaf)
            return leaf.Panel == target ? replacer(leaf) : leaf;

        if (node is CutterDockSplit split)
        {
            split.First = ReplaceLeaf(split.First, target, replacer);
            split.Second = ReplaceLeaf(split.Second, target, replacer);
        }
        return node;
    }
}
