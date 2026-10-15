using FabricDataGenerator;
using FlowOps.Domain.Directory;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

Console.WriteLine("FlowOps Fabric synthetic data generator");
Console.WriteLine("========================================");
Console.WriteLine($"Seed: {GenerationOptions.Seed}");
Console.WriteLine($"History window: {GenerationOptions.HistoryStart:yyyy-MM-dd} .. {GenerationOptions.HistoryEnd:yyyy-MM-dd}");
Console.WriteLine();
Console.WriteLine("Starting an ephemeral, throwaway PostgreSQL container (Docker required)...");

// A brand-new container per run, destroyed at the end — this never touches flowops_dev or any
// Neon database. Applying the real EF Core migrations guarantees the schema (tables, columns,
// constraints, indexes) is byte-for-byte what the actual application uses.
var postgres = new PostgreSqlBuilder("postgres:17-alpine")
    .WithDatabase("fabric_demo_source")
    .WithUsername("fabric_demo")
    .WithPassword("fabric_demo_password")
    .Build();

await postgres.StartAsync();

var optionsBuilder = new DbContextOptionsBuilder<FlowOpsDbContext>()
    .UseNpgsql(postgres.GetConnectionString())
    .UseSnakeCaseNamingConvention();

await using (var migrationContext = new FlowOpsDbContext(optionsBuilder.Options))
{
    Console.WriteLine("Applying real FlowOps EF Core migrations...");
    await migrationContext.Database.MigrateAsync();
}

var random = new Random(GenerationOptions.Seed);
var seedTime = new DateTimeOffset(GenerationOptions.HistoryEnd.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
var referenceBuilder = new ReferenceDataBuilder(random, seedTime);

await using (var db = new FlowOpsDbContext(optionsBuilder.Options))
{
    Console.WriteLine();
    Console.WriteLine("Seeding reference data (organization, teams, categories, projects, users)...");

    var organization = referenceBuilder.BuildOrganization();
    db.Organizations.Add(organization);
    await db.SaveChangesAsync();

    var teams = referenceBuilder.BuildTeams(organization.Id);
    db.Teams.AddRange(teams);
    await db.SaveChangesAsync();

    var categories = referenceBuilder.BuildCategories(teams);
    db.Categories.AddRange(categories);
    await db.SaveChangesAsync();

    var projects = referenceBuilder.BuildProjects(organization.Id);
    db.Projects.AddRange(projects);
    await db.SaveChangesAsync();

    var slaConfigurations = ReferenceDataBuilder.BuildSlaConfigurations();
    db.SlaConfigurations.AddRange(slaConfigurations);
    await db.SaveChangesAsync();

    var users = referenceBuilder.BuildUsers();

    // ApplicationUser rows are inserted directly (no UserManager/password hashing) — this dataset
    // is exported for Fabric analytics, never used to sign in, so Identity's authentication
    // machinery is unnecessary overhead for 20,000+ rows downstream.
    var applicationUsers = users.Select(u => new ApplicationUser
    {
        Id = u.Id,
        UserName = u.Email,
        NormalizedUserName = u.Email.ToUpperInvariant(),
        Email = u.Email,
        NormalizedEmail = u.Email.ToUpperInvariant(),
        EmailConfirmed = true,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        DisplayName = u.DisplayName,
        JobTitle = u.JobTitle,
        IsActive = true,
        RegistrationApprovedAt = seedTime,
    }).ToList();
    db.Users.AddRange(applicationUsers);
    await db.SaveChangesAsync();

    var organizationMemberships = referenceBuilder.BuildOrganizationMemberships(organization.Id, users);
    db.OrganizationMemberships.AddRange(organizationMemberships);
    await db.SaveChangesAsync();

    var teamMembers = referenceBuilder.BuildTeamMembers(teams, users);
    db.TeamMembers.AddRange(teamMembers);
    await db.SaveChangesAsync();

    Console.WriteLine($"  {teams.Count} teams, {categories.Count} categories, {projects.Count} projects, {users.Count} users, {teamMembers.Count} team memberships, {slaConfigurations.Count} SLA configurations.");

    Console.WriteLine();
    Console.WriteLine($"Generating {GenerationOptions.TicketCount:N0} tickets (each with a realistic, domain-validated lifecycle)...");

    var categoryTeamIndex = categories.Select(c => teams.FindIndex(t => t.Id == c.TeamId)).ToArray();
    var workContext = TicketWorkContext.Build(teams, categories, categoryTeamIndex, projects, users, slaConfigurations, teamMembers);
    var ticketGenerator = new TicketGenerator(workContext, random);

    const int batchSize = 250;
    var generated = 0;
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    foreach (var ticket in ticketGenerator.Generate(GenerationOptions.TicketCount))
    {
        db.Tickets.Add(ticket);
        generated++;

        if (generated % batchSize == 0)
        {
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            if (generated % 2000 == 0)
            {
                Console.WriteLine($"  {generated:N0} / {GenerationOptions.TicketCount:N0} tickets ({stopwatch.Elapsed:mm\\:ss} elapsed)");
            }
        }
    }

    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    Console.WriteLine($"  done in {stopwatch.Elapsed:mm\\:ss}.");
}

var outputDirectory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", GenerationOptions.OutputDirectoryName);
outputDirectory = Path.GetFullPath(outputDirectory);

Console.WriteLine();
Console.WriteLine($"Exporting tables to CSV: {outputDirectory}");
await CsvExporter.ExportAllAsync(postgres.GetConnectionString(), outputDirectory);

Console.WriteLine();
Console.WriteLine("Running validation...");
var report = await Validator.RunAsync(postgres.GetConnectionString());
Console.WriteLine();
Console.WriteLine(report);

var reportPath = Path.Combine(outputDirectory, "validation_report.txt");
await File.WriteAllTextAsync(reportPath, report);
Console.WriteLine();
Console.WriteLine($"Validation report written to {reportPath}");

Console.WriteLine();
Console.WriteLine("Destroying the ephemeral PostgreSQL container...");
await postgres.DisposeAsync();
Console.WriteLine("Done. No data was written to flowops_dev or any Neon database.");
