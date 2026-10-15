namespace FabricDataGenerator;

/// <summary>
/// Fictional data pools for names, titles, and comment text. Nothing here is or resembles a real
/// person, ticket, or organization — nouns and templates are deliberately generic IT-service-desk
/// vocabulary.
/// </summary>
internal static class Names
{
    public static readonly string[] FirstNames =
    [
        "Ava", "Liam", "Maya", "Noah", "Zoe", "Ethan", "Grace", "Mason", "Lily", "Owen",
        "Chloe", "Lucas", "Nina", "Aiden", "Ruby", "Leo", "Ella", "Kai", "Isla", "Theo",
        "Priya", "Omar", "Sofia", "Hugo", "Amara", "Dev", "Freya", "Jonas", "Mira", "Samir",
    ];

    public static readonly string[] LastNames =
    [
        "Bennett", "Carter", "Diaz", "Ellison", "Foster", "Grant", "Hayes", "Iyer", "Jensen",
        "Kaur", "Lindqvist", "Moreno", "Nakamura", "Okafor", "Pereira", "Quinn", "Reyes",
        "Sato", "Tremblay", "Ueda", "Vargas", "Walsh", "Yilmaz", "Zimmer", "Abara",
    ];

    public static readonly string[] JobTitles =
    [
        "IT Support Specialist", "Service Desk Analyst", "Systems Administrator",
        "Application Support Engineer", "Network Engineer", "Infrastructure Engineer",
        "Operations Coordinator", "Support Team Lead", "Business Analyst", "Software Engineer",
    ];

    public static readonly string[] ProjectNames =
    [
        "Cloud Migration Wave 2",
        "ERP Rollout",
        "Customer Portal Revamp",
        "Network Upgrade Phase 2",
        "Office Relocation IT Setup",
        "Security Hardening Initiative",
        "Legacy App Decommission",
        "Self-Service Portal Launch",
    ];

    public static readonly string[] TicketTitleTemplates =
    [
        "Unable to access {0} application",
        "Password reset requested for {0}",
        "New hardware request: {0}",
        "Network connectivity issue in {0}",
        "Software installation request: {0}",
        "Account locked out after failed logins",
        "VPN connection drops intermittently",
        "Printer not responding on {0} floor",
        "Request to provision access to {0}",
        "{0} application returning error on save",
        "Slow performance reported on {0}",
        "Email delivery delayed for {0} messages",
        "Request for laptop replacement",
        "Data export request for {0} report",
        "Configuration change requested for {0}",
        "Incident: {0} service outage",
        "Onboarding setup for new starter",
        "Offboarding access removal request",
        "Mobile device enrollment issue",
        "Shared drive permissions incorrect for {0}",
    ];

    public static readonly string[] TitleFillers =
    [
        "Finance", "HR", "the CRM", "Billing", "the intranet", "Sales", "Marketing",
        "the file server", "the ticketing portal", "the reporting suite", "Payroll",
        "the customer database", "the mobile app", "Warehouse", "Support",
    ];

    public static readonly string[] DescriptionTemplates =
    [
        "The user reported this issue during business hours and it is affecting their daily work. Initial details have been captured from the request form.",
        "Reported via the service desk. The requester needs this resolved to continue normal operations without further disruption.",
        "Issue reproduced during triage. Affects a single user; no other reports of the same symptom have been received yet.",
        "Request submitted through the self-service portal. Standard fulfillment process applies unless additional approval is required.",
        "Escalated from the first-line queue after initial troubleshooting did not resolve the symptom.",
        "Routine request as part of standard onboarding/offboarding procedure for this department.",
        "Recurring issue reported previously; may be related to a known intermittent condition being tracked separately.",
    ];

    public static readonly string[] CommentTemplates =
    [
        "Investigating the reported issue.",
        "Access has been provisioned.",
        "User confirmed the issue is resolved.",
        "Escalating to the application support team.",
        "Waiting for confirmation from the requester.",
        "Reproduced the issue in a test environment.",
        "Applied the standard fix and monitoring for recurrence.",
        "Requested additional information from the user.",
        "Vendor has been contacted for further diagnosis.",
        "Scheduled a maintenance window to apply the fix.",
        "Confirmed root cause; preparing a permanent fix.",
        "Temporary workaround provided while a fix is developed.",
        "Following up after 24 hours with no response from requester.",
        "Closing out after successful verification with the user.",
        "Re-checked configuration; no anomalies found.",
    ];

    public static readonly string[] ResolutionNoteTemplates =
    [
        "Issue resolved after applying the standard fix procedure and confirming with the user.",
        "Completed the requested change; user has confirmed everything is working as expected.",
        "Provided a workaround while the underlying issue is tracked for a permanent fix.",
        "No fault could be found after investigation; issue did not reproduce during testing.",
        "Duplicate of an existing ticket already being handled by the team.",
        "Request withdrawn by the requester before completion.",
    ];

    public static readonly string[] ReopenReasons =
    [
        "Issue recurred after the initial fix.",
        "User reports the same symptom returned within a day.",
        "Follow-up batch process failed the same way.",
        "Additional occurrence reported by a different user on the same system.",
    ];

    public static readonly string[] HoldReasons =
    [
        "Waiting for vendor response.",
        "Waiting for requester to confirm details.",
        "Pending approval from department manager.",
        "Waiting on a scheduled maintenance window.",
        "Blocked on a dependency from another team.",
    ];
}
