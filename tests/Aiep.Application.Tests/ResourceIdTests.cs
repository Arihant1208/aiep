using Aiep.Application.Ids;

namespace Aiep.Application.Tests;

public sealed class ResourceIdTests
{
    [Fact]
    public void Round_trips_and_sorts_like_the_uuid_bytes()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).Append(Guid.CreateVersion7()).ToList();

        var formatted = ids.Select(id => ResourceId.Format(ResourceId.Workspace, id)).ToList();

        Assert.All(formatted, f => Assert.Matches("^ws_[0-7][0-9A-HJKMNP-TV-Z]{25}\\z", f));
        Assert.Equal(ids, formatted.Select(f => ResourceId.TryParse(ResourceId.Workspace, f, out var id) ? id : Guid.Empty));

        // String order == big-endian byte order, so UUIDv7 IDs sort by creation time (to the millisecond).
        var byBytes = ids.OrderBy(id => Convert.ToHexString(id.ToByteArray(bigEndian: true)), StringComparer.Ordinal)
            .Select(id => ResourceId.Format(ResourceId.Workspace, id));
        Assert.Equal(byBytes, formatted.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Ids_from_different_milliseconds_sort_by_creation_time()
    {
        var earlier = Guid.CreateVersion7(DateTimeOffset.UnixEpoch.AddYears(56));
        var later = Guid.CreateVersion7(DateTimeOffset.UnixEpoch.AddYears(56).AddMilliseconds(1));

        Assert.True(string.CompareOrdinal(
            ResourceId.Format(ResourceId.Workspace, earlier), ResourceId.Format(ResourceId.Workspace, later)) < 0);
    }

    [Fact]
    public void Parsing_is_case_insensitive_in_the_body_but_not_the_prefix()
    {
        var id = Guid.CreateVersion7();
        var formatted = ResourceId.Format(ResourceId.Organization, id);

        Assert.True(ResourceId.TryParse(ResourceId.Organization, "org_" + formatted[4..].ToLowerInvariant(), out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(ResourceId.TryParse(ResourceId.Organization, "ORG_" + formatted[4..], out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ws_")]
    [InlineData("ws_0000000000000000000000000")]
    [InlineData("ws_00000000000000000000000000")] // Guid.Empty
    [InlineData("ws_80000000000000000000000000")] // > 128 bits
    [InlineData("ws_0000000000000000000000000I")] // excluded letter
    [InlineData("ws-01JC00000000000000000000000")]
    [InlineData("ws_01JC0000000000000000000000 ")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void Rejects_malformed_values(string? value) =>
        Assert.False(ResourceId.TryParse(ResourceId.Workspace, value, out _));

    [Fact]
    public void Rejects_an_id_with_another_resources_prefix()
    {
        var orgId = ResourceId.Format(ResourceId.Organization, Guid.CreateVersion7());

        Assert.False(ResourceId.TryParse(ResourceId.Workspace, orgId, out _));
    }
}
