using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace Gil.Ontology;

/// <summary>The tree could not be built: the file breaks a structural rule. Warnings, by contrast, still load.</summary>
public sealed class OntologyException : Exception
{
    public OntologyException()
    {
    }

    public OntologyException(string message)
        : base(message)
    {
    }

    public OntologyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A loaded tree and the things a person should look at before trusting it.</summary>
public sealed record LoadedOntology(Node Root, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads and writes the tree's YAML — the editable source of truth, reviewed as diffs. Runtime statistics never
/// go in it. Writing omits fields equal to their defaults so generated and hand-written files diff cleanly.
/// </summary>
public static partial class OntologyYaml
{
    // A word that appears in more than this share of the siblings' descriptions is shared vocabulary, not
    // evidence that two siblings overlap.
    private const double CommonVocabularyShare = 0.3;
    private const int MinimumSharedWords = 3;

    private static readonly Dictionary<string, int> Capacity = new() { ["digits"] = 9, ["letters"] = 25 };

    public static LoadedOntology Load(string path) => Parse(File.ReadAllText(path));

    public static LoadedOntology Parse(string yaml)
    {
        var raw = new DeserializerBuilder().Build().Deserialize<object>(yaml) as IDictionary<object, object>
            ?? throw new OntologyException("the document is not a mapping");
        var warnings = new List<string>();
        var root = ReadNode(raw, warnings, [], inheritedScheme: "letters");
        return new LoadedOntology(root, warnings);
    }

    public static string Dump(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        // Quote any string a YAML 1.1 reader would take for a boolean, null or number (`no`, `on`, `1e3`) — the file is
        // read by other implementations, and a bare `no` silently becomes false there.
        return new SerializerBuilder().WithQuotingNecessaryStrings(quoteYaml1_1Strings: true).Build().Serialize(NodeMap(root));
    }

    /// <summary>
    /// Adds a warning when two siblings' descriptions share three or more distinctive words: the judge may not
    /// be able to tell them apart. Not an error — how much overlap is acceptable is a domain call.
    /// </summary>
    public static void WarnOnSiblingOverlap(Node node, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(warnings);
        var described = node.Children.Count > 0
            ? node.Children.Where(c => c.Description.Length > 0).Select(c => (c.Id, Words: ContentWords(c.Description))).ToList()
            : node.Habits.Where(o => o.Description.Length > 0).Select(o => (o.Id, Words: ContentWords(o.Description))).ToList();
        if (described.Count < 2)
        {
            return;
        }

        var frequency = described.SelectMany(d => d.Words).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var ceiling = Math.Max(2, (int)(described.Count * CommonVocabularyShare));
        var distinctive = frequency.Where(p => p.Value <= ceiling).Select(p => p.Key).ToHashSet();
        for (var i = 0; i < described.Count; i++)
        {
            for (var j = i + 1; j < described.Count; j++)
            {
                var shared = described[i].Words.Intersect(described[j].Words).Where(distinctive.Contains).Order(StringComparer.Ordinal).ToList();
                if (shared.Count >= MinimumSharedWords)
                {
                    warnings.Add(
                        $"{node.Id}: sibling descriptions overlap — {described[i].Id} and {described[j].Id} share "
                        + $"[{string.Join(", ", shared)}]; the judge may not tell them apart.");
                }
            }
        }
    }

    private static Node ReadNode(IDictionary<object, object> raw, List<string> warnings, HashSet<string> seen, string inheritedScheme)
    {
        var id = Required(raw, "id");
        Claim(seen, id);
        var scheme = Text(raw, "label_scheme") ?? inheritedScheme;
        var childrenRaw = Sequence(raw, "children");
        var optionsRaw = Sequence(raw, "options");
        if (childrenRaw.Count > 0 && optionsRaw.Count > 0)
        {
            throw new OntologyException(
                $"{id}: children and options together — a node asks about categories or about concrete candidates, not both");
        }

        var children = childrenRaw.Select(c => ReadNode(Mapping(c, id), warnings, seen, scheme)).ToList();
        var options = optionsRaw.Select(o => ReadHabit(Mapping(o, id), seen)).ToList();
        var count = Math.Max(children.Count, options.Count);
        if (!Capacity.TryGetValue(scheme, out var capacity))
        {
            throw new OntologyException($"{id}: unknown label scheme {scheme}");
        }

        if (count > capacity)
        {
            throw new OntologyException(
                $"{id}: {count} candidates, but the {scheme} scheme labels at most {capacity} — split the node or use another scheme");
        }

        var node = new Node
        {
            Id = id,
            Label = Text(raw, "label") ?? id,
            Description = Text(raw, "description") ?? "",
            Children = children,
            Habits = options,
            Origin = Text(raw, "origin") ?? "seed",
            Kind = Text(raw, "kind") ?? "concept",
            LabelScheme = scheme,
        };
        WarnOnSiblingOverlap(node, warnings);
        return node;
    }

    private static Habit ReadHabit(IDictionary<object, object> raw, HashSet<string> seen)
    {
        var id = Required(raw, "id");
        Claim(seen, id);
        var kindText = Text(raw, "kind") ?? "answer";
        var kind = kindText switch
        {
            "answer" => HabitKind.Answer,
            "template" => HabitKind.Template,
            "procedure" => HabitKind.Procedure,
            _ => throw new OntologyException($"{id}: unknown kind {kindText}"),
        };
        var text = Text(raw, "text");
        var template = Text(raw, "template");
        var steps = Text(raw, "steps");
        var slots = Sequence(raw, "slots")
            .Select(s => Mapping(s, id))
            .Select(s => new Slot(Required(s, "name"), Text(s, "instruction") ?? "", Text(s, "fixed")))
            .ToList();

        switch (kind)
        {
            case HabitKind.Answer when string.IsNullOrEmpty(text):
                throw new OntologyException($"{id}: an answer needs text");
            case HabitKind.Template when string.IsNullOrEmpty(template):
                throw new OntologyException($"{id}: a template needs template text");
            case HabitKind.Template when slots.Count == 0:
                throw new OntologyException($"{id}: a template needs slots");
            case HabitKind.Template:
                var missing = Placeholder().Matches(template!).Select(m => m.Groups[1].Value)
                    .Except(slots.Select(s => s.Name)).Order(StringComparer.Ordinal).ToList();
                if (missing.Count > 0)
                {
                    throw new OntologyException($"{id}: template blanks without a slot — [{string.Join(", ", missing)}]");
                }

                break;
            case HabitKind.Procedure when string.IsNullOrEmpty(steps):
                throw new OntologyException($"{id}: a procedure needs steps");
        }

        return new Habit
        {
            Id = id,
            Kind = kind,
            Label = Text(raw, "label") ?? id,
            Description = Text(raw, "description") ?? "",
            Text = text,
            Template = template,
            Slots = slots,
            Steps = steps,
            Origin = Text(raw, "origin") ?? "seed",
        };
    }

    private static Dictionary<string, object> NodeMap(Node node)
    {
        var map = new Dictionary<string, object>
        {
            ["id"] = node.Id,
            ["label"] = node.Label,
            ["description"] = node.Description,
            ["label_scheme"] = node.LabelScheme,
        };
        if (node.Origin != "seed")
        {
            map["origin"] = node.Origin;
        }

        if (node.Kind != "concept")
        {
            map["kind"] = node.Kind;
        }

        if (node.Children.Count > 0)
        {
            map["children"] = node.Children.Select(NodeMap).ToList();
        }

        if (node.Habits.Count > 0)
        {
            map["options"] = node.Habits.Select(HabitMap).ToList();
        }

        return map;
    }

    private static Dictionary<string, object> HabitMap(Habit option)
    {
        var map = new Dictionary<string, object>
        {
            ["id"] = option.Id,
            ["kind"] = option.Kind.ToString().ToLowerInvariant(),
            ["label"] = option.Label,
            ["description"] = option.Description,
        };
        if (option.Text is not null)
        {
            map["text"] = option.Text;
        }

        if (option.Template is not null)
        {
            map["template"] = option.Template;
        }

        if (option.Slots.Count > 0)
        {
            map["slots"] = option.Slots.Select(SlotMap).ToList();
        }

        if (option.Steps is not null)
        {
            map["steps"] = option.Steps;
        }

        map["origin"] = option.Origin;
        return map;
    }

    private static Dictionary<string, object> SlotMap(Slot slot)
    {
        var map = new Dictionary<string, object> { ["name"] = slot.Name, ["instruction"] = slot.Instruction };
        if (slot.Fixed is not null)
        {
            map["fixed"] = slot.Fixed;
        }

        return map;
    }

    private static HashSet<string> ContentWords(string text) =>
        Separator().Split(text.ToLowerInvariant()).Where(w => w.Length > 1).ToHashSet();

    private static void Claim(HashSet<string> seen, string id)
    {
        if (!seen.Add(id))
        {
            throw new OntologyException($"duplicate id: {id}");
        }
    }

    private static string Required(IDictionary<object, object> raw, string key) =>
        Text(raw, key) ?? throw new OntologyException($"missing {key}");

    private static string? Text(IDictionary<object, object> raw, string key) =>
        raw.TryGetValue(key, out var value) && value is not null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    private static List<object> Sequence(IDictionary<object, object> raw, string key) =>
        raw.TryGetValue(key, out var value) && value is List<object> items ? items : [];

    private static IDictionary<object, object> Mapping(object item, string parent) =>
        item as IDictionary<object, object> ?? throw new OntologyException($"{parent}: expected a mapping");

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"[\s·,—\-()]+")]
    private static partial Regex Separator();
}
