namespace BusinessCard.Api.Common;

public record PageResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public static class Paging
{
    public const int MaxPageSize = 1000;

    public static (int Page, int PageSize) Normalize(int page, int pageSize)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 20 : Math.Min(pageSize, MaxPageSize);
        return (page, pageSize);
    }
}
