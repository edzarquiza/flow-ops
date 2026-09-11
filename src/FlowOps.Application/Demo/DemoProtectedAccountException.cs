namespace FlowOps.Application.Demo;

/// <summary>Thrown by <see cref="DemoProtectionPolicy"/> when a caller attempts to delete, rename,
/// role-change, or password-change a seeded demo persona account (CLAUDE.md §14).</summary>
public sealed class DemoProtectedAccountException(string message) : Exception(message);
