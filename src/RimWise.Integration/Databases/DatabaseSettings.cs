namespace RimWise.Integration.Databases;

public enum DatabaseSourceMode { Disabled, GitHub, Website, LocalFile }

/// <summary>Only a player-selected source is read. This configuration is never exposed as an AI tool.</summary>
public sealed record DatabaseSource(DatabaseSourceMode Mode, string GitHub, string Website = "", string LocalFile = "")
{
    public string Location => Mode switch
    {
        DatabaseSourceMode.GitHub => GitHub.Trim(), DatabaseSourceMode.Website => Website.Trim(),
        DatabaseSourceMode.LocalFile => LocalFile.Trim(), _ => ""
    };
    internal void Validate()
    {
        if (!Enum.IsDefined(Mode) || new[] { GitHub, Website, LocalFile }.Any(s => s is null || s.Length > 4096 || s.Any(char.IsControl)))
            throw new InvalidDataException("数据库来源设置无效。");
        if (Mode != DatabaseSourceMode.Disabled && Location.Length == 0)
            throw new InvalidDataException("请填写所选数据库来源。");
        if (Mode == DatabaseSourceMode.GitHub) DatabaseSourceReader.GitHubRepository(Location);
        if (Mode == DatabaseSourceMode.Website) DatabaseSourceReader.WebUri(Location);
        if (Mode == DatabaseSourceMode.LocalFile && !Path.IsPathFullyQualified(Location))
            throw new InvalidDataException("请选择本地数据库文件的完整路径。");
    }
}

public sealed record DatabaseSettings(DatabaseSource Community, DatabaseSource Workshop)
{
    public static DatabaseSettings Default => new(
        new(DatabaseSourceMode.GitHub, "https://github.com/RimSort/Community-Rules-Database"),
        new(DatabaseSourceMode.GitHub, "https://github.com/RimSort/Steam-Workshop-Database"));
    internal void Validate()
    {
        if (Community is null || Workshop is null) throw new InvalidDataException("数据库来源设置不完整。");
        Community.Validate(); Workshop.Validate();
    }
}

internal sealed record DatabaseSelection(int Schema, DatabaseSettings Settings, string CurrentId, string? PreviousId);
