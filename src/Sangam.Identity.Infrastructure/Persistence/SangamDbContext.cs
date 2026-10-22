using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence;

/// <summary>
/// The single identity database. ASP.NET Core Identity user tables (no Identity roles —
/// platform operators and app admins have their own tables), Sangam's tenancy tables, and
/// OpenIddict's application / authorization / scope / token tables. All names snake_case.
/// </summary>
public sealed class SangamDbContext : IdentityUserContext<SangamUser, Guid>, IDataProtectionKeyContext
{
    /// <summary>Initialises the context.</summary>
    /// <param name="options">EF Core options.</param>
    public SangamDbContext(DbContextOptions<SangamDbContext> options)
        : base(options)
    {
    }

    /// <summary>Organisation types (reference data).</summary>
    public DbSet<OrgType> OrgTypes => Set<OrgType>();

    /// <summary>Organisations.</summary>
    public DbSet<Organisation> Organisations => Set<Organisation>();

    /// <summary>Partner apps.</summary>
    public DbSet<App> Apps => Set<App>();

    /// <summary>App-defined roles.</summary>
    public DbSet<Role> Roles => Set<Role>();

    /// <summary>Organisation memberships.</summary>
    public DbSet<OrgMembership> OrgMemberships => Set<OrgMembership>();

    /// <summary>App grants.</summary>
    public DbSet<AppGrant> AppGrants => Set<AppGrant>();

    /// <summary>rc.14: the id each application knows each person by (pairwise subject ids).</summary>
    public DbSet<AppSubject> AppSubjects => Set<AppSubject>();

    /// <summary>Consents.</summary>
    public DbSet<Consent> Consents => Set<Consent>();

    /// <summary>Platform operators.</summary>
    public DbSet<PlatformOperator> PlatformOperators => Set<PlatformOperator>();

    /// <summary>App admins.</summary>
    public DbSet<AppAdmin> AppAdmins => Set<AppAdmin>();

    /// <summary>Audit events (append-only).</summary>
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    /// <summary>Which applications received tokens in which session (PR-20).</summary>
    public DbSet<SessionApp> SessionApps => Set<SessionApp>();

    /// <summary>Back-channel logouts waiting to be delivered (PR-20).</summary>
    public DbSet<LogoutNotification> LogoutNotifications => Set<LogoutNotification>();

    /// <summary>Electronic-signature requests and their outcomes (PR-17).</summary>
    public DbSet<SignatureRequest> SignatureRequests => Set<SignatureRequest>();

    /// <summary>Customisation settings by level (PR-19).</summary>
    public DbSet<CustomisationSetting> Customisations => Set<CustomisationSetting>();

    /// <summary>Uploaded logos (PR-19).</summary>
    public DbSet<BrandingAsset> BrandingAssets => Set<BrandingAsset>();

    /// <summary>E-mail and SMS templates by level and language (PR-19).</summary>
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();

    /// <summary>SMS sent by Sangam, without numbers or codes (PR-15).</summary>
    public DbSet<SmsMessage> SmsMessages => Set<SmsMessage>();

    /// <summary>Passkeys registered to accounts (PR-14).</summary>
    public DbSet<PasskeyCredential> PasskeyCredentials => Set<PasskeyCredential>();

    /// <summary>Pending passkey ceremonies, single use (PR-14).</summary>
    public DbSet<PasskeyChallenge> PasskeyChallenges => Set<PasskeyChallenge>();

    /// <summary>Invitations by e-mail to an organisation and role (PR-13).</summary>
    public DbSet<Invitation> Invitations => Set<Invitation>();

    /// <summary>Pending and completed changes of e-mail address (OI-022).</summary>
    public DbSet<EmailChangeRequest> EmailChangeRequests => Set<EmailChangeRequest>();

    /// <summary>Monitoring: one row per minute, host, metric and tag (D-H).</summary>
    public DbSet<MetricPoint> MetricPoints => Set<MetricPoint>();

    /// <summary>Monitoring: alert conditions, open and resolved (D-H).</summary>
    public DbSet<MonitoringAlert> MonitoringAlerts => Set<MonitoringAlert>();

    /// <summary>Monitoring: what each host reports about itself, such as the breached-password list (V-10).</summary>
    public DbSet<HostReport> HostReports => Set<HostReport>();

    /// <summary>Wrong passwords for addresses with no account, shared by every replica (D-L, R7).</summary>
    public DbSet<UnknownAddressAttempt> UnknownAddressAttempts => Set<UnknownAddressAttempt>();

    /// <summary>Development only: every host's captured e-mails and texts, for <c>/dev/outbox</c> (V-11).</summary>
    public DbSet<DevOutboxMessage> DevOutbox => Set<DevOutboxMessage>();

    /// <summary>The grievance log (D-D): every DPDP grievance, with its deadlines and history.</summary>
    public DbSet<Grievance> Grievances => Set<Grievance>();

    /// <summary>The steps of each grievance (D-D).</summary>
    public DbSet<GrievanceEntry> GrievanceEntries => Set<GrievanceEntry>();

    /// <summary>SAML service providers (PR-22).</summary>
    public DbSet<SamlServiceProvider> SamlServiceProviders => Set<SamlServiceProvider>();

    /// <summary>SAML requests being answered, and answered ones for replay detection (PR-22).</summary>
    public DbSet<SamlRequest> SamlRequests => Set<SamlRequest>();

    /// <summary>Changes for applications, for SCIM and webhooks (PR-23, PR-24).</summary>
    public DbSet<AppEvent> AppEvents => Set<AppEvent>();

    /// <summary>Applications' SCIM servers (PR-23).</summary>
    public DbSet<ScimTarget> ScimTargets => Set<ScimTarget>();

    /// <summary>People as each SCIM server knows them.</summary>
    public DbSet<ScimUserLink> ScimUserLinks => Set<ScimUserLink>();

    /// <summary>Groups Sangam created on SCIM servers.</summary>
    public DbSet<ScimGroupLink> ScimGroupLinks => Set<ScimGroupLink>();

    /// <summary>People Sangam put in those groups.</summary>
    public DbSet<ScimGroupMember> ScimGroupMembers => Set<ScimGroupMember>();

    /// <summary>The SCIM delivery log.</summary>
    public DbSet<ScimDelivery> ScimDeliveries => Set<ScimDelivery>();

    /// <summary>Applications' webhook endpoints (PR-24).</summary>
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();

    /// <summary>The webhook delivery log.</summary>
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    /// <summary>Custom user attributes applications define (PR-25).</summary>
    public DbSet<UserAttributeDefinition> UserAttributeDefinitions => Set<UserAttributeDefinition>();

    /// <summary>People's values of them.</summary>
    public DbSet<UserAttributeValue> UserAttributeValues => Set<UserAttributeValue>();

    /// <summary>Identities verified against a government record (PR-26).</summary>
    public DbSet<IdentityVerification> IdentityVerifications => Set<IdentityVerification>();

    /// <summary>Custom claims applications ask for (PR-25).</summary>
    public DbSet<AppClaimMapping> AppClaimMappings => Set<AppClaimMapping>();

    /// <summary>Support requests to reset two-step sign-in, with their cooling-off period (D-K).</summary>
    public DbSet<MfaResetRequest> MfaResetRequests => Set<MfaResetRequest>();

    /// <summary>A person's requests to merge two of their accounts (rc.7, SGM-914 section 9).</summary>
    public DbSet<AccountMergeRequest> AccountMergeRequests => Set<AccountMergeRequest>();

    /// <summary>The ASP.NET Core data-protection key ring, shared by every host (OI-037).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>One-time codes (email verification, password reset, sign-in).</summary>
    public DbSet<OneTimeCode> OneTimeCodes => Set<OneTimeCode>();

    /// <summary>Browser sessions.</summary>
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    /// <summary>Login IDs given up recently, kept out of reach of others for a while (rc.11).</summary>
    public DbSet<LoginIdRelease> LoginIdReleases => Set<LoginIdRelease>();

    /// <summary>Client secrets made by applications' owners on the partner console (rc.11).</summary>
    public DbSet<ClientSecret> ClientSecrets => Set<ClientSecret>();

    /// <summary>rc.15: public keys applications sign in with (<c>private_key_jwt</c>).</summary>
    public DbSet<ClientKey> ClientKeys => Set<ClientKey>();

    /// <summary>rc.15: client assertions already accepted, until they expire, so none is accepted twice.</summary>
    public DbSet<ClientAssertionUse> ClientAssertionUses => Set<ClientAssertionUse>();

    /// <summary>rc.11 (R6): decisions taken in applications' access reviews.</summary>
    public DbSet<AccessReviewDecision> AccessReviewDecisions => Set<AccessReviewDecision>();

    /// <summary>rc.12: customer and branch admin rights, and emergency-review rights.</summary>
    public DbSet<OrgAdmin> OrgAdmins => Set<OrgAdmin>();

    /// <summary>rc.12: people being added as staff, with what the admin entered and an activation code.</summary>
    public DbSet<StaffInvite> StaffInvites => Set<StaffInvite>();

    /// <summary>rc.12: organisations a person is being added to, waiting for them to join or for the start date.</summary>
    public DbSet<PendingMembership> PendingMemberships => Set<PendingMembership>();

    /// <summary>rc.12: one-time codes people make to be added to an organisation.</summary>
    public DbSet<JoinCode> JoinCodes => Set<JoinCode>();

    /// <summary>rc.12: checks admins ran before adding someone.</summary>
    public DbSet<StaffCheck> StaffChecks => Set<StaffCheck>();

    /// <summary>rc.12 (T1): shared ward devices enrolled by customer and branch admins.</summary>
    public DbSet<SharedDevice> SharedDevices => Set<SharedDevice>();

    /// <summary>rc.12 (T1): the people who use each shared device, and their locks.</summary>
    public DbSet<SharedDevicePerson> SharedDevicePeople => Set<SharedDevicePerson>();

    /// <summary>rc.12 (T1): people's PINs for unlocking shared devices.</summary>
    public DbSet<UserPin> UserPins => Set<UserPin>();

    /// <summary>rc.12 (M12): uses of emergency access, and their reviews.</summary>
    public DbSet<EmergencyAccess> EmergencyAccesses => Set<EmergencyAccess>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(AssemblyReference.Assembly);
        builder.UseOpenIddict();

        // OpenIddict names its tables explicitly, which bypasses the snake_case convention.
        builder.Entity<OpenIddictEntityFrameworkCoreApplication>().ToTable("openiddict_applications");
        builder.Entity<OpenIddictEntityFrameworkCoreAuthorization>().ToTable("openiddict_authorizations");
        builder.Entity<OpenIddictEntityFrameworkCoreScope>().ToTable("openiddict_scopes");
        builder.Entity<OpenIddictEntityFrameworkCoreToken>().ToTable("openiddict_tokens");
    }
}
