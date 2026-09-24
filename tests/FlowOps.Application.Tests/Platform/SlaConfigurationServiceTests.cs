using FlowOps.Application.Platform;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Platform;

/// <summary>
/// Phase 30D (ADR-0034) against real PostgreSQL: editing, adding, and removing SLA configuration
/// rows. <see cref="SlaConfiguration"/> is genuinely global (ADR-0015) and this whole test
/// collection shares one process-lifetime database (<see cref="PostgresFixture"/>) — every test
/// here is careful to only ever touch a row it created itself, and to remove it again before
/// returning, so a mutation here can never change what an unrelated, concurrently-running test's
/// own ticket resolves its SLA target against. The four seeded priority-default rows are read
/// from, and their "cannot be deleted" refusal is exercised, but their own values are never edited.
/// </summary>
[Collection("Postgres")]
public sealed class SlaConfigurationServiceTests
{
    private readonly PostgresFixture _fixture;

    public SlaConfigurationServiceTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly PlatformAdminIdentity Admin = new(Guid.NewGuid(), "Platform Test Admin");

    [Fact]
    public async Task GetAllAsync_IncludesEveryPriorityDefaultRow()
    {
        await using var context = _fixture.CreateContext();
        var service = new SlaConfigurationService(context);

        var configurations = await service.GetAllAsync(Admin);

        foreach (var priority in Enum.GetValues<Priority>())
        {
            Assert.Contains(configurations, c => c.IsDefault && c.Priority == priority);
        }
    }

    [Fact]
    public async Task CreateOverrideAsync_ThenUpdateAsync_ThenDeleteOverrideAsync_RoundTrips()
    {
        await using var context = _fixture.CreateContext();
        var service = new SlaConfigurationService(context);

        var create = await service.CreateOverrideAsync(Admin, WorkType.Incident, Priority.Critical, 120, 75);
        Assert.True(create.Succeeded);

        try
        {
            var afterCreate = await service.GetAllAsync(Admin);
            var created = Assert.Single(afterCreate, c => c.WorkType == WorkType.Incident && c.Priority == Priority.Critical);
            Assert.False(created.IsDefault);
            Assert.Equal(120, created.TargetMinutes);
            Assert.Equal(75, created.RiskThresholdPercent);

            var update = await service.UpdateAsync(Admin, created.Id, 90, 60);
            Assert.True(update.Succeeded);

            var afterUpdate = await service.GetAllAsync(Admin);
            var updated = afterUpdate.Single(c => c.Id == created.Id);
            Assert.Equal(90, updated.TargetMinutes);
            Assert.Equal(60, updated.RiskThresholdPercent);

            var delete = await service.DeleteOverrideAsync(Admin, created.Id);
            Assert.True(delete.Succeeded);

            var afterDelete = await service.GetAllAsync(Admin);
            Assert.DoesNotContain(afterDelete, c => c.Id == created.Id);
        }
        finally
        {
            // Belt-and-suspenders: if an assertion above failed before the real delete ran, this
            // whole shared-database test collection must not be left with a stray override.
            await using var cleanup = _fixture.CreateContext();
            var stray = await cleanup.SlaConfigurations.SingleOrDefaultAsync(c => c.WorkType == WorkType.Incident && c.Priority == Priority.Critical);
            if (stray is not null)
            {
                cleanup.SlaConfigurations.Remove(stray);
                await cleanup.SaveChangesAsync();
            }
        }
    }

    [Fact]
    public async Task CreateOverrideAsync_DuplicateWorkTypeAndPriority_Fails()
    {
        await using var context = _fixture.CreateContext();
        var service = new SlaConfigurationService(context);
        var first = await service.CreateOverrideAsync(Admin, WorkType.ServiceRequest, Priority.High, 200, 80);
        Assert.True(first.Succeeded);

        try
        {
            var second = await service.CreateOverrideAsync(Admin, WorkType.ServiceRequest, Priority.High, 300, 70);

            Assert.False(second.Succeeded);
            Assert.NotNull(second.Error);
            var configurations = await service.GetAllAsync(Admin);
            Assert.Single(configurations, c => c.WorkType == WorkType.ServiceRequest && c.Priority == Priority.High);
        }
        finally
        {
            await using var cleanup = _fixture.CreateContext();
            var row = await cleanup.SlaConfigurations.SingleOrDefaultAsync(c => c.WorkType == WorkType.ServiceRequest && c.Priority == Priority.High);
            if (row is not null)
            {
                cleanup.SlaConfigurations.Remove(row);
                await cleanup.SaveChangesAsync();
            }
        }
    }

    [Theory]
    [InlineData(0, 80)] // target minutes below the minimum
    [InlineData(100, 0)] // risk threshold below the minimum
    [InlineData(100, 101)] // risk threshold above the maximum
    public async Task CreateOverrideAsync_OutOfBoundsInput_FailsAndPersistsNothing(int targetMinutes, int riskThresholdPercent)
    {
        await using var context = _fixture.CreateContext();
        var service = new SlaConfigurationService(context);

        var result = await service.CreateOverrideAsync(Admin, WorkType.Incident, Priority.Low, targetMinutes, riskThresholdPercent);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        var exists = await context.SlaConfigurations.AsNoTracking().AnyAsync(c => c.WorkType == WorkType.Incident && c.Priority == Priority.Low);
        Assert.False(exists);
    }

    [Theory]
    [InlineData(0, 80)]
    [InlineData(100, 0)]
    [InlineData(100, 101)]
    public async Task UpdateAsync_OutOfBoundsInput_FailsAndLeavesTheRowUnchanged(int targetMinutes, int riskThresholdPercent)
    {
        await using var context = _fixture.CreateContext();
        var service = new SlaConfigurationService(context);
        var create = await service.CreateOverrideAsync(Admin, WorkType.ServiceRequest, Priority.Medium, 500, 80);
        Assert.True(create.Succeeded);

        try
        {
            var configurations = await service.GetAllAsync(Admin);
            var created = configurations.Single(c => c.WorkType == WorkType.ServiceRequest && c.Priority == Priority.Medium);

            var result = await service.UpdateAsync(Admin, created.Id, targetMinutes, riskThresholdPercent);

            Assert.False(result.Succeeded);
            var afterFailedUpdate = (await service.GetAllAsync(Admin)).Single(c => c.Id == created.Id);
            Assert.Equal(500, afterFailedUpdate.TargetMinutes);
            Assert.Equal(80, afterFailedUpdate.RiskThresholdPercent);
        }
        finally
        {
            await using var cleanup = _fixture.CreateContext();
            var row = await cleanup.SlaConfigurations.SingleOrDefaultAsync(c => c.WorkType == WorkType.ServiceRequest && c.Priority == Priority.Medium);
            if (row is not null)
            {
                cleanup.SlaConfigurations.Remove(row);
                await cleanup.SaveChangesAsync();
            }
        }
    }

    [Fact] // SLA-RULE-01: every priority must always resolve to something — the default row can
           // never be removed through this service.
    public async Task DeleteOverrideAsync_ADefaultRow_IsRefused()
    {
        await using var context = _fixture.CreateContext();
        var service = new SlaConfigurationService(context);
        var configurations = await service.GetAllAsync(Admin);
        var defaultRow = configurations.Single(c => c.IsDefault && c.Priority == Priority.Critical);

        var result = await service.DeleteOverrideAsync(Admin, defaultRow.Id);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        var stillExists = await context.SlaConfigurations.AsNoTracking().AnyAsync(c => c.Id == defaultRow.Id);
        Assert.True(stillExists);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_Fails()
    {
        await using var context = _fixture.CreateContext();
        var service = new SlaConfigurationService(context);

        var result = await service.UpdateAsync(Admin, 999_999, 100, 80);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DeleteOverrideAsync_UnknownId_Fails()
    {
        await using var context = _fixture.CreateContext();
        var service = new SlaConfigurationService(context);

        var result = await service.DeleteOverrideAsync(Admin, 999_999);

        Assert.False(result.Succeeded);
    }
}
