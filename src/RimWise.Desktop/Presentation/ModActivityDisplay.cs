namespace RimWise.Desktop.Presentation;

internal static class ModActivityDisplay
{
    internal static string AddedTooltip(DateTimeOffset? timestamp) => timestamp is { } time
        ? $"近期新增\n首次发现：{time.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "近期新增\n新增时间未记录";
    internal static string UpdatedTooltip(DateTimeOffset? timestamp) => timestamp is { } time
        ? $"近期更新\n最近更新：{time.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "近期更新\n更新时间未读取";
}
