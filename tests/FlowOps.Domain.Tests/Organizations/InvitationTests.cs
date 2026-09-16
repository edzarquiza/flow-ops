using FlowOps.Domain;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using Xunit;

namespace FlowOps.Domain.Tests.Organizations;

public class InvitationTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static Invitation ValidInvitation(TimeSpan? lifetime = null) =>
        Invitation.Create(1, "alice@example.com", "ALICE@EXAMPLE.COM", "hash", UserRole.Agent, Guid.NewGuid(), Now, lifetime);

    [Fact]
    public void Create_ValidInput_SetsExpiryFromLifetime()
    {
        var invitation = ValidInvitation(TimeSpan.FromDays(7));

        Assert.Equal(Now.AddDays(7), invitation.ExpiresAt);
        Assert.False(invitation.IsAccepted);
    }

    [Fact]
    public void Create_DefaultLifetime_IsSevenDays()
    {
        var invitation = ValidInvitation();

        Assert.Equal(Now + Invitation.DefaultLifetime, invitation.ExpiresAt);
    }

    [Fact]
    public void Create_BlankEmail_Throws()
    {
        var ex = Assert.Throws<DomainRuleException>(() =>
            Invitation.Create(1, "   ", "", "hash", UserRole.Agent, Guid.NewGuid(), Now));
        Assert.Equal("ORG-RULE-09", ex.RuleCode);
    }

    [Fact]
    public void Create_BlankTokenHash_Throws()
    {
        var ex = Assert.Throws<DomainRuleException>(() =>
            Invitation.Create(1, "alice@example.com", "ALICE@EXAMPLE.COM", "", UserRole.Agent, Guid.NewGuid(), Now));
        Assert.Equal("ORG-RULE-08", ex.RuleCode);
    }

    [Fact]
    public void Create_NonPositiveLifetime_Throws()
    {
        var ex = Assert.Throws<DomainRuleException>(() => ValidInvitation(TimeSpan.Zero));
        Assert.Equal("ORG-RULE-10", ex.RuleCode);
    }

    [Fact]
    public void Accept_BeforeExpiry_Succeeds()
    {
        var invitation = ValidInvitation(TimeSpan.FromDays(1));

        invitation.Accept(Now.AddHours(1));

        Assert.True(invitation.IsAccepted);
        Assert.Equal(Now.AddHours(1), invitation.AcceptedAt);
    }

    [Fact] // ORG-RULE-08: single-use.
    public void Accept_AlreadyAccepted_Throws()
    {
        var invitation = ValidInvitation(TimeSpan.FromDays(1));
        invitation.Accept(Now.AddHours(1));

        var ex = Assert.Throws<DomainRuleException>(() => invitation.Accept(Now.AddHours(2)));
        Assert.Equal("ORG-RULE-08", ex.RuleCode);
    }

    [Fact] // ORG-RULE-10: an expired invitation can never be accepted, even with an otherwise valid token.
    public void Accept_AfterExpiry_Throws()
    {
        var invitation = ValidInvitation(TimeSpan.FromDays(1));

        var ex = Assert.Throws<DomainRuleException>(() => invitation.Accept(Now.AddDays(2)));
        Assert.Equal("ORG-RULE-10", ex.RuleCode);
        Assert.False(invitation.IsAccepted); // rejected, not silently marked accepted
    }

    [Fact]
    public void IsExpired_ExactlyAtExpiry_IsTrue()
    {
        var invitation = ValidInvitation(TimeSpan.FromDays(1));

        Assert.True(invitation.IsExpired(Now.AddDays(1)));
        Assert.False(invitation.IsExpired(Now.AddDays(1).AddSeconds(-1)));
    }
}
