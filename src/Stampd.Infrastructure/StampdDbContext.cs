using Microsoft.EntityFrameworkCore;

using Stampd.Core.Entities;
using Stampd.Core.Tenancy;
using Stampd.Infrastructure.Identity;

namespace Stampd.Infrastructure;

/// <summary>
/// The Stampd persistence root. Provider-agnostic: SQL Server and PostgreSQL adapters
/// configure their column types in companion projects (<c>Stampd.Infrastructure.SqlServer</c>,
/// <c>Stampd.Infrastructure.Postgres</c>).
/// </summary>
public class StampdDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public StampdDbContext(DbContextOptions<StampdDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        _tenantContext = tenantContext;
    }

    public DbSet<DocumentTemplate> DocumentTemplates => Set<DocumentTemplate>();
    public DbSet<TemplateRecipientRole> TemplateRecipientRoles => Set<TemplateRecipientRole>();
    public DbSet<TemplateField> TemplateFields => Set<TemplateField>();
    public DbSet<SigningRequest> SigningRequests => Set<SigningRequest>();
    public DbSet<Recipient> Recipients => Set<Recipient>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SignedDocumentRecord> SignedDocumentRecords => Set<SignedDocumentRecord>();
    public DbSet<OtpChallengeEntity> OtpChallenges => Set<OtpChallengeEntity>();
    public DbSet<BulkSendJob> BulkSendJobs => Set<BulkSendJob>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    /// <summary>
    /// Resolves the tenant id used by every global query filter. Lifted to a method so
    /// EF Core's query translator can call it per-query rather than capturing a constant.
    /// </summary>
    internal Guid CurrentTenantId() => _tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Apply all IEntityTypeConfiguration<T> implementations in this assembly.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StampdDbContext).Assembly);

        // Global tenant query filters. Declared centrally so the lambdas close over the
        // live DbContext-bound CurrentTenantId() accessor — EF Core re-evaluates this per
        // query, parameterising the resulting SQL.
        //
        // A current tenant of Guid.Empty (e.g. from a cross-tenant maintenance job)
        // disables the filter for that query path.
        modelBuilder.Entity<DocumentTemplate>()
            .HasQueryFilter(t => CurrentTenantId() == Guid.Empty || t.TenantId == CurrentTenantId());

        modelBuilder.Entity<TemplateRecipientRole>()
            .HasQueryFilter(r => CurrentTenantId() == Guid.Empty || r.DocumentTemplate!.TenantId == CurrentTenantId());

        modelBuilder.Entity<TemplateField>()
            .HasQueryFilter(f => CurrentTenantId() == Guid.Empty || f.DocumentTemplate!.TenantId == CurrentTenantId());

        modelBuilder.Entity<SigningRequest>()
            .HasQueryFilter(r => CurrentTenantId() == Guid.Empty || r.TenantId == CurrentTenantId());

        modelBuilder.Entity<Recipient>()
            .HasQueryFilter(p => CurrentTenantId() == Guid.Empty || p.SigningRequest!.TenantId == CurrentTenantId());

        modelBuilder.Entity<AuditEvent>()
            .HasQueryFilter(e => CurrentTenantId() == Guid.Empty || e.TenantId == CurrentTenantId());

        modelBuilder.Entity<SignedDocumentRecord>()
            .HasQueryFilter(d => CurrentTenantId() == Guid.Empty || d.TenantId == CurrentTenantId());

        modelBuilder.Entity<BulkSendJob>()
            .HasQueryFilter(j => CurrentTenantId() == Guid.Empty || j.TenantId == CurrentTenantId());

        modelBuilder.Entity<WebhookEndpoint>()
            .HasQueryFilter(w => CurrentTenantId() == Guid.Empty || w.TenantId == CurrentTenantId());

        modelBuilder.Entity<WebhookDelivery>()
            .HasQueryFilter(d => CurrentTenantId() == Guid.Empty || d.TenantId == CurrentTenantId());

        base.OnModelCreating(modelBuilder);
    }

    public override int SaveChanges()
    {
        UpdateAuditableFields();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateAuditableFields();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Stamps timestamps, refreshes concurrency tokens, and blocks accidental cross-tenant
    /// writes when a tenant scope is active.
    /// </summary>
    private void UpdateAuditableFields()
    {
        var now = DateTimeOffset.UtcNow;
        var currentTenant = _tenantContext.TenantId;
        var crossTenantAllowed = currentTenant == Guid.Empty;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            switch (entry.Entity)
            {
                case DocumentTemplate template:
                    if (entry.State == EntityState.Added)
                    {
                        if (template.Id == Guid.Empty)
                        {
                            template.Id = Guid.NewGuid();
                        }

                        if (template.CreatedAtUtc == default)
                        {
                            template.CreatedAtUtc = now;
                        }

                        template.TenantId = EnsureTenant(template.TenantId, currentTenant, crossTenantAllowed);
                        // CreatedAtUtc is immutable, so the epoch shadow is only stamped here.
                        template.CreatedAtUtcEpochMs = template.CreatedAtUtc.ToUnixTimeMilliseconds();
                    }

                    template.UpdatedAtUtc = now;
                    template.ConcurrencyToken = Guid.NewGuid();
                    break;

                case SigningRequest request:
                    if (entry.State == EntityState.Added)
                    {
                        if (request.Id == Guid.Empty)
                        {
                            request.Id = Guid.NewGuid();
                        }

                        if (request.CreatedAtUtc == default)
                        {
                            request.CreatedAtUtc = now;
                        }

                        request.TenantId = EnsureTenant(request.TenantId, currentTenant, crossTenantAllowed);
                    }

                    request.ConcurrencyToken = Guid.NewGuid();
                    break;

                case AuditEvent auditEvent:
                    if (entry.State == EntityState.Added)
                    {
                        if (auditEvent.Id == Guid.Empty)
                        {
                            auditEvent.Id = Guid.NewGuid();
                        }

                        if (auditEvent.OccurredAtUtc == default)
                        {
                            auditEvent.OccurredAtUtc = now;
                        }

                        auditEvent.TenantId = EnsureTenant(auditEvent.TenantId, currentTenant, crossTenantAllowed);
                    }
                    else if (entry.State == EntityState.Modified)
                    {
                        // Audit events are append-only; the only legal mutation is the
                        // GDPR redaction flag and the personal fields it nulls out.
                        var redactedEntry = entry.Property(nameof(AuditEvent.IsRedacted));
                        if (!redactedEntry.IsModified || auditEvent.IsRedacted == false)
                        {
                            throw new InvalidOperationException(
                                "AuditEvent rows are append-only; the only permitted modification is GDPR redaction.");
                        }
                    }

                    break;

                case BulkSendJob job:
                    if (entry.State == EntityState.Added)
                    {
                        if (job.Id == Guid.Empty)
                        {
                            job.Id = Guid.NewGuid();
                        }

                        if (job.CreatedAtUtc == default)
                        {
                            job.CreatedAtUtc = now;
                        }

                        job.TenantId = EnsureTenant(job.TenantId, currentTenant, crossTenantAllowed);
                        job.CreatedAtUtcEpochMs = job.CreatedAtUtc.ToUnixTimeMilliseconds();
                    }

                    break;

                case WebhookEndpoint endpoint:
                    if (entry.State == EntityState.Added)
                    {
                        if (endpoint.Id == Guid.Empty)
                        {
                            endpoint.Id = Guid.NewGuid();
                        }

                        if (endpoint.CreatedAtUtc == default)
                        {
                            endpoint.CreatedAtUtc = now;
                        }

                        endpoint.TenantId = EnsureTenant(endpoint.TenantId, currentTenant, crossTenantAllowed);
                    }

                    break;

                case WebhookDelivery delivery:
                    if (entry.State == EntityState.Added)
                    {
                        if (delivery.Id == Guid.Empty)
                        {
                            delivery.Id = Guid.NewGuid();
                        }

                        if (delivery.CreatedAtUtc == default)
                        {
                            delivery.CreatedAtUtc = now;
                        }

                        delivery.TenantId = EnsureTenant(delivery.TenantId, currentTenant, crossTenantAllowed);
                    }

                    // NextAttemptAtUtc changes on every retry reschedule, so keep the
                    // epoch shadow in sync on both Added and Modified.
                    delivery.NextAttemptAtUtcEpochMs = delivery.NextAttemptAtUtc.ToUnixTimeMilliseconds();

                    break;

                case SignedDocumentRecord signed:
                    if (entry.State == EntityState.Added)
                    {
                        if (signed.Id == Guid.Empty)
                        {
                            signed.Id = Guid.NewGuid();
                        }

                        if (signed.SignedAtUtc == default)
                        {
                            signed.SignedAtUtc = now;
                        }

                        signed.TenantId = EnsureTenant(signed.TenantId, currentTenant, crossTenantAllowed);
                    }
                    else if (entry.State == EntityState.Modified)
                    {
                        throw new InvalidOperationException(
                            "SignedDocumentRecord rows are immutable once persisted.");
                    }

                    break;
            }
        }
    }

    private static Guid EnsureTenant(Guid existing, Guid currentTenant, bool crossTenantAllowed)
    {
        if (existing != Guid.Empty)
        {
            return existing;
        }

        if (crossTenantAllowed)
        {
            throw new InvalidOperationException(
                "Cannot persist a tenanted entity without a TenantId when no tenant is in scope.");
        }

        return currentTenant;
    }
}
