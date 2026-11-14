using Aiep.Application.Authorization;

namespace Aiep.Application.Tests.Authorization;

public sealed class AuthorizationTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid WsA = Guid.CreateVersion7();
    private static readonly Guid WsB = Guid.CreateVersion7();
    private readonly AuthorizationService _authz = new();

    private static AuthorizationSubject Member(
        OrgRole role,
        bool active = true,
        (Guid Workspace, WorkspaceRole Role)? grant = null,
        IReadOnlyList<string>? keyScope = null) => new()
        {
            SubjectId = Guid.CreateVersion7(),
            OrganizationId = Org,
            IsActiveMember = active,
            OrgRole = role,
            WorkspaceGrants = grant is { } g
                ? new Dictionary<Guid, WorkspaceRole> { [g.Workspace] = g.Role }
                : new Dictionary<Guid, WorkspaceRole>(),
            KeyScope = keyScope,
        };

    [Fact]
    public void Owner_has_every_permission_everywhere()
    {
        var owner = Member(OrgRole.Owner);

        Assert.Equal(new SortedSet<string>(Permission.All), new SortedSet<string>(owner.EffectiveAtOrganization()));
        Assert.Equal(new SortedSet<string>(Permission.All), new SortedSet<string>(owner.EffectiveInWorkspace(WsA)));
    }

    [Fact]
    public void Admin_has_everything_except_ownership_transfer()
    {
        var admin = Member(OrgRole.Admin);

        Assert.DoesNotContain(Permission.OrgTransfer, admin.EffectiveAtOrganization());
        Assert.Contains(Permission.MembersManage, admin.EffectiveAtOrganization());
        Assert.Contains(Permission.WorkspacesManage, admin.EffectiveInWorkspace(WsA));
    }

    [Fact]
    public void Auditor_reads_organization_wide_but_cannot_act()
    {
        var auditor = Member(OrgRole.Auditor);
        var atOrg = auditor.EffectiveAtOrganization();

        Assert.Contains(Permission.AuditRead, atOrg);
        Assert.Contains(Permission.RunsRead, atOrg);
        Assert.DoesNotContain(Permission.RunsReadResult, atOrg);
        Assert.DoesNotContain(Permission.RunsSubmit, atOrg);
        Assert.DoesNotContain(Permission.MembersManage, atOrg);
    }

    [Fact]
    public void A_plain_member_has_no_access_even_in_a_workspace()
    {
        // Invariant: organization membership alone grants no workspace access (FR-005).
        var member = Member(OrgRole.Member);

        Assert.Empty(member.EffectiveAtOrganization());
        Assert.Empty(member.EffectiveInWorkspace(WsA));
        Assert.Equal(AuthorizationFailure.MissingPermission,
            _authz.AuthorizeWorkspace(member, WsA, Permission.RunsRead).Failure);
    }

    [Fact]
    public void A_workspace_grant_applies_only_to_its_workspace()
    {
        var developer = Member(OrgRole.Member, grant: (WsA, WorkspaceRole.Developer));

        Assert.True(_authz.AuthorizeWorkspace(developer, WsA, Permission.RunsSubmit).IsAllowed);
        Assert.Contains(Permission.RunsSubmit, developer.EffectiveInWorkspace(WsA));

        // Same person, a workspace they have no grant on: nothing.
        Assert.Empty(developer.EffectiveInWorkspace(WsB));
        Assert.False(_authz.AuthorizeWorkspace(developer, WsB, Permission.RunsSubmit).IsAllowed);
    }

    [Fact]
    public void Owner_and_admin_are_workspace_admins_on_every_workspace_without_a_grant()
    {
        foreach (var role in new[] { OrgRole.Owner, OrgRole.Admin })
        {
            var subject = Member(role);
            Assert.True(_authz.AuthorizeWorkspace(subject, WsA, Permission.GrantsManage).IsAllowed);
            Assert.True(_authz.AuthorizeWorkspace(subject, WsB, Permission.RunsReadResult).IsAllowed);
        }
    }

    [Fact]
    public void An_api_key_cannot_exceed_its_scope()
    {
        // A developer whose key is scoped to submitting runs only.
        var key = Member(OrgRole.Member, grant: (WsA, WorkspaceRole.Developer), keyScope: [Permission.RunsSubmit]);

        var effective = key.EffectiveInWorkspace(WsA);
        Assert.Equal(new[] { Permission.RunsSubmit }, effective);
        Assert.True(_authz.AuthorizeWorkspace(key, WsA, Permission.RunsSubmit).IsAllowed);
        // The role would allow it, but the key scope does not.
        Assert.False(_authz.AuthorizeWorkspace(key, WsA, Permission.RunsReadResult).IsAllowed);
    }

    [Fact]
    public void A_key_loses_power_when_its_owner_loses_access()
    {
        var scope = new[] { Permission.RunsSubmit };

        var whileGranted = Member(OrgRole.Member, grant: (WsA, WorkspaceRole.Developer), keyScope: scope);
        Assert.True(_authz.AuthorizeWorkspace(whileGranted, WsA, Permission.RunsSubmit).IsAllowed);

        // Owner's workspace grant removed: the key's scope intersects an empty role set → nothing.
        var afterGrantRemoved = Member(OrgRole.Member, keyScope: scope);
        Assert.Empty(afterGrantRemoved.EffectiveInWorkspace(WsA));

        // Owner's membership removed entirely: same result, reported as not-a-member (404).
        var afterRemoval = Member(OrgRole.Member, active: false, grant: (WsA, WorkspaceRole.Developer), keyScope: scope);
        Assert.Empty(afterRemoval.EffectiveInWorkspace(WsA));
        Assert.Equal(AuthorizationFailure.NotAMember,
            _authz.AuthorizeWorkspace(afterRemoval, WsA, Permission.RunsSubmit).Failure);
    }

    [Fact]
    public void A_non_member_is_denied_as_not_a_member()
    {
        var stranger = Member(OrgRole.Owner, active: false);

        var result = _authz.AuthorizeOrganization(stranger, Permission.OrgRead);
        Assert.False(result.IsAllowed);
        Assert.Equal(AuthorizationFailure.NotAMember, result.Failure);
    }

    [Fact]
    public void A_member_missing_a_permission_is_denied_as_forbidden()
    {
        var auditor = Member(OrgRole.Auditor);

        var result = _authz.AuthorizeOrganization(auditor, Permission.MembersManage);
        Assert.False(result.IsAllowed);
        Assert.Equal(AuthorizationFailure.MissingPermission, result.Failure);
    }

    [Fact]
    public void An_unknown_required_permission_is_a_bug_not_a_denial()
    {
        var owner = Member(OrgRole.Owner);

        Assert.Throws<ArgumentException>(() => _authz.AuthorizeOrganization(owner, "runs:teleport"));
    }

    [Fact]
    public void No_one_can_delegate_a_permission_they_lack()
    {
        var developerEffective = RolePermissions.ForWorkspaceRole(WorkspaceRole.Developer);

        Assert.True(AuthorizationInvariants.CanDelegate(developerEffective, [Permission.RunsSubmit, Permission.RunsCancel]));
        // Developer has no grants:manage, so cannot grant it.
        Assert.False(AuthorizationInvariants.CanDelegate(developerEffective, [Permission.GrantsManage]));
    }

    [Fact]
    public void Key_scope_must_stay_within_the_owners_permissions()
    {
        var ownerEffective = RolePermissions.ForWorkspaceRole(WorkspaceRole.Developer);

        Assert.Empty(AuthorizationInvariants.ScopeExceeding(ownerEffective, [Permission.RunsSubmit, Permission.DefinitionsWrite]));

        var exceeding = AuthorizationInvariants.ScopeExceeding(
            ownerEffective, [Permission.RunsSubmit, Permission.GrantsManage, "runs:teleport"]);
        Assert.Equal(
            new[] { Permission.GrantsManage, "runs:teleport" },
            exceeding.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_last_owner_cannot_be_removed()
    {
        var soleOwner = Guid.CreateVersion7();
        var otherOwner = Guid.CreateVersion7();

        Assert.True(AuthorizationInvariants.WouldRemoveLastOwner([soleOwner], soleOwner));
        Assert.False(AuthorizationInvariants.WouldRemoveLastOwner([soleOwner, otherOwner], soleOwner));
        // Removing a non-owner never trips the invariant.
        Assert.False(AuthorizationInvariants.WouldRemoveLastOwner([soleOwner], Guid.CreateVersion7()));
    }
}
