using Tenantry.Core.Directory;

namespace Tenantry.Tests;

/// <summary>A small hand-written directory, for tests that need to know exactly who is in it.</summary>
sealed class FakeDirectory(params UserSummary[] users) : ITenantDirectory
{
    public string TenantName => "Test tenant";

    public Task<IReadOnlyList<UserSummary>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserSummary>>(users);

    public static UserSummary User(
        string id, string name, string? department = null, string? title = null,
        bool enabled = true, UserKind kind = UserKind.Member, string[]? licenses = null)
    {
        var alias = name.ToLowerInvariant().Replace(' ', '.');
        return new UserSummary(id, name, $"{alias}@example.org", $"{alias}@example.org", department, title,
            enabled, kind, DateTimeOffset.UnixEpoch, null, licenses ?? []);
    }

    public static FakeDirectory Standard() => new(
        User("1", "Ada Lovelace", "Finance", "Controller", licenses: ["E3"]),
        User("2", "Alan Turing", "IT", "Engineer", licenses: ["E5"]),
        User("3", "Grace Hopper", "IT", "Director", enabled: false, licenses: ["E3"]),
        User("4", "Guest Person", kind: UserKind.Guest));
}
