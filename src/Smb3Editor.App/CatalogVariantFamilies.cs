using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smb3Editor.App;

internal sealed record CatalogVariantFamily(string Id, string FamilySortId, string NeighborhoodSortId, string Name, bool HidePreview);

internal sealed record ItemGroupRule(
    string Id,
    string Name,
    string Kind,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string>? Exclude = null,
    IReadOnlyList<int>? Tilesets = null,
    IReadOnlyList<string>? ObjectTypes = null,
    bool HidePreview = false,
    string? SortGroup = null);

internal sealed record ItemsConfig(int FormatVersion, IReadOnlyList<ItemGroupRule> Groups)
{
    public static ItemsConfig Empty { get; } = new(1, []);
}

/// <summary>
/// Loads editable, presentation-only item families. Object families remain
/// scoped to their SMB3 object set; each selected variant retains its own
/// fixed/variable command encoding.
/// </summary>
internal sealed class CatalogVariantFamilies
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly IReadOnlyList<ItemGroupRule> _groups;

    private CatalogVariantFamilies(IReadOnlyList<ItemGroupRule> groups) => _groups = groups;

    public static CatalogVariantFamilies Load(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = $"Items configuration was not found at {path}. Related items will remain separate.";
                return new CatalogVariantFamilies([]);
            }

            var config = JsonSerializer.Deserialize<ItemsConfig>(File.ReadAllBytes(path), JsonOptions) ?? ItemsConfig.Empty;
            if (config.FormatVersion != 1)
                throw new InvalidDataException($"Unsupported items config format {config.FormatVersion}.");
            if (config.Groups.Any(group => string.IsNullOrWhiteSpace(group.Id) ||
                                           string.IsNullOrWhiteSpace(group.Name) ||
                                           group.Patterns is null || group.Patterns.Count == 0))
                throw new InvalidDataException("Every item group needs an id, name, and at least one pattern.");
            if (config.Groups.GroupBy(group => group.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new InvalidDataException("Item group ids must be unique.");
            return new CatalogVariantFamilies(config.Groups);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            error = $"Items configuration could not be loaded: {ex.Message} Related items will remain separate.";
            return new CatalogVariantFamilies([]);
        }
    }

    public CatalogVariantFamily? Find(int tileset, bool isEnemy, bool isVariable, string name)
    {
        var kind = isEnemy ? "sprite" : "object";
        var objectType = isVariable ? "variable" : "fixed";
        var rule = _groups.FirstOrDefault(group =>
            (group.Kind.Equals("any", StringComparison.OrdinalIgnoreCase) || group.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)) &&
            (group.Tilesets is null || group.Tilesets.Count == 0 || group.Tilesets.Contains(tileset)) &&
            (isEnemy || group.ObjectTypes is null || group.ObjectTypes.Count == 0 || group.ObjectTypes.Contains(objectType, StringComparer.OrdinalIgnoreCase)) &&
            group.Patterns.Any(pattern => GlobMatches(name, pattern)) &&
            !(group.Exclude?.Any(pattern => GlobMatches(name, pattern)) ?? false));
        if (rule is null) return null;

        var id = isEnemy
            ? $"sprite:{rule.Id}"
            : $"object:{tileset}:{rule.Id}";
        return new CatalogVariantFamily(id, rule.Id, rule.SortGroup ?? rule.Id, rule.Name, rule.HidePreview);
    }

    private static bool GlobMatches(string value, string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return false;
        // '*' is a simple, case-insensitive wildcard at any position. This
        // keeps editable rules such as "Wood*Platform" straightforward.
        var expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
