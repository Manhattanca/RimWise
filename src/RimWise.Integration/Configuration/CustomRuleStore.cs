using RimWise.Core;

namespace RimWise.Integration.Configuration;

public sealed class CustomRuleStore(string applicationDirectory)
{
    private sealed record Document(int Schema, CustomOrderRule[] Rules);
    private readonly AppJsonFile<Document> file = new(applicationDirectory, "order-rules", 512 * 1024);
    public IReadOnlyList<CustomOrderRule> Load()
    {
        var doc = file.Load();
        if (doc is null) return [];
        if (doc.Schema != 1 || doc.Rules is null || doc.Rules.Length > 2000)
            throw new InvalidDataException("自定义规则文件无效。");
        return doc.Rules.Select(rule => (rule ?? throw new InvalidDataException("规则不能为空。")).Normalize()).Distinct().ToArray();
    }
    public void Save(IEnumerable<CustomOrderRule> rules)
    {
        var list = rules.Take(2001).Select(rule => rule.Normalize()).Distinct().ToArray();
        if (list.Length > 2000) throw new InvalidDataException("自定义规则最多 2000 条。");
        file.Save(new(1, list));
    }
}
