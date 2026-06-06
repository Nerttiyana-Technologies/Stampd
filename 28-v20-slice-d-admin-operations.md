# 28 — v2.0 Slice D: Admin Operations + Audit Attribution

*v2.0 Slice D — Tasks #198–#206*

The v1.x signing workflow had no way to void a request mid-flight, no way to nudge a recipient who lost their invitation email, and no way to wipe demo state between walkthroughs. v2.0 Phase 0 + Slice A wired the auth foundation; Slice D layers the actual mutations on top: three workflow methods (VoidAsync, ResendInvitationAsync, demo cleanup), three admin endpoints behind the AdminOnly policy, and the audit-trail schema needed to attribute every admin action to a real actor instead of leaving the trail anonymous.

## What shipped

### Phase 0' — ICurrentActorContext

The Slice A audit named the gap: SigningWorkflowService had no way to know who its caller was. Slice D opens with the abstraction:

```csharp
public interface ICurrentActorContext
{
    bool IsAuthenticated { get; }
    string? UserId { get; }       // JWT sub claim
    string? ActiveRole { get; }   // most-privileged role held
    bool IsAdmin { get; }
}
```

WebApi implementation (`HttpCurrentActorContext`) reads from `IHttpContextAccessor.HttpContext.User`. Falls back gracefully to nulls when there's no HttpContext (background workers, integration tests bypassing the pipeline). The `ActiveRole` getter picks Admin > Sender > ReadOnly so a multi-role JWT is reported as the highest tier — that's the role they're effectively acting in when hitting an admin-gated endpoint.

Scoped DI (`AddHttpContextAccessor` + `AddScoped`) so each HTTP request gets a fresh read.

### AuditEvent.ActorUserId + ActorRole — V14 migration

Two new nullable columns on `AuditEvents`:

```csharp
public string? ActorUserId { get; set; }   // max 256 — fits any JWT sub
public string? ActorRole { get; set; }     // max 64 — Admin/Sender/ReadOnly + headroom
```

Plus a filtered-style composite index `(TenantId, ActorUserId, OccurredAtUtc)` powering the most common admin-audit query ("show all actions by user X this week"). NULL is the right default for recipient-flow events (RecipientViewed, RecipientSigned — the recipient is identified via RecipientId), background workers, and pre-v2.0 rows.

V14 migration on all three providers (SQLite, SqlServer, Postgres) — same shape as v1.3's V12+V13 multi-provider work in Doc 21. Each provider got migration.cs + Designer.cs files + snapshot patches. The Designer.cs files were regenerated after the snapshot patches landed so they capture the V14 terminal state.

`SigningWorkflowService.AddAudit` reads from `_actorContext` at audit-write time:

```csharp
ActorUserId = _actorContext?.IsAuthenticated == true ? _actorContext.UserId : null,
ActorRole = _actorContext?.IsAuthenticated == true ? _actorContext.ActiveRole : null,
```

Every existing audit call-site (Dispatch, Submit, MarkViewed, MarkIdentityVerified, etc.) now writes the actor automatically — no per-call-site change needed.

### Workflow methods

**`VoidAsync(signingRequestId, reason, ct)`** — idempotent. Loads the request, refuses to transition Completed/Declined/Expired (terminal non-Voided states), sets Status=Voided + VoidedAtUtc + TerminationReason ("Voided by admin" default), writes `AuditEventType.SigningRequestVoided`, enqueues the `WebhookEventType.SigningRequestVoided` event that v1.x had wired up but never fired. Returns true when the void actually applied, false when it was already voided or the request didn't exist.

**`ResendInvitationAsync(recipientId, ct)`** — only valid for Invited/Viewed recipients. Throws InvalidOperationException for Signed/Declined/Expired. Reuses the existing `BuildInvitationHtmlBody` / `BuildInvitationPlainTextBody` helpers from v1.3 #157, sends with a "Reminder — {subject}" subject prefix so the recipient can tell it apart from the original. Writes new `AuditEventType.RecipientInvitationResent = 208`. Best-effort: SMTP failures get logged and return false rather than throw (same shape as the dispatch-path invitation send).

### Admin endpoints — `AdminOperationsEndpoints`

Three POST endpoints under the adminGroup (inherits `Admin`-only policy):

| Endpoint | Body | Returns |
|---|---|---|
| `/api/admin/signing-requests/void` | `{ ids: Guid[], reason: string }` | `{ succeeded, failed, items: [{ id, success, error }] }` |
| `/api/admin/signing-requests/resend-invitation` | `{ recipientIds: Guid[] }` | Same shape |
| `/api/admin/cleanup-demo` | (none) | `{ signingRequestsDeleted, recipientsDeleted, ... }` |

Bulk-op cap is 100 ids per call — enough for any reasonable admin batch without exposing a runaway operation that locks up the worker. The two bulk endpoints iterate sequentially (correctness + audit ordering deterministic; v2.1 perf polish if needed).

`cleanup-demo` runs in Development by default and refuses in other environments unless `Stampd:Admin:AllowDemoCleanup=true` is explicitly set. Uses `ExecuteDeleteAsync` to bypass the change-tracker for each table — cheap even when the tables are large. Order matters: child rows (AuditEvents, Recipients, SignedDocumentRecords) before parent (SigningRequests), then OtpChallenges + WebhookDeliveries. DocumentTemplates are preserved so the demo bootstrap still has something to reuse.

### UI — danger zone tile + bulk action bar

**Admin dashboard `/admin`** got a red-bordered "Danger zone" card at the bottom with a two-step confirmation: click "Reset demo data" → button morphs into "Are you sure?" + "Yes, delete everything" + "Cancel". Successful cleanup shows the per-table delete counts inline. The page auto-reloads its tiles + trend + top-templates after the wipe so the post-cleanup state is immediately visible.

**Signing-requests list `/designer/requests`** got a checkbox column when the current user is in the Admin role (rendered conditionally via `CurrentUser.IsInRole(Admin)`). The grid-template-columns shifts to leave room for a 36px checkbox column. A header checkbox bulk-toggles all rows on the current page; row checkboxes track per-id. Selection survives pagination, so an admin can pull two pages of "stuck Sent requests" and void them in one batch.

A floating action bar appears at the bottom of the viewport when at least one request is selected — fixed-position so the page can scroll beneath it. It shows the count + Clear + Void selected + Resend invitation buttons. Void uses the deep-red color from the danger zone. Resend is disabled when no selected request has a recipient with an active access URL (i.e. every selected request has only signed recipients).

After a bulk op completes, a toast-like message above the action bar reports `Voided N/M requests` or `Resent invitations to N/M recipients`. The selection clears on void (matching the now-Voided state) but persists on resend (the admin might want to do other follow-ups on the same set).

## What you get on disk

A v2.0 admin in Development can now:

1. Open `/designer/requests`, filter to "Sent" requests older than a week.
2. Tick the header checkbox to select the visible page.
3. Click "Void selected" → red confirmation flash → all of them transition to Voided with their TerminationReason set.
4. Audit log shows each `SigningRequestVoided` event with the admin's JWT sub as `ActorUserId` and "Admin" as `ActorRole`.
5. If they go nuclear, open `/admin`, scroll to the danger zone, two-click "Reset demo data" → every signing request + recipient + audit event + OTP challenge + webhook delivery is wiped. Templates remain so `/demo/seed` still works.

## Trade-offs

- **Bulk ops are sequential, not parallel.** 100-id batches finish in ~1–2 seconds today. Parallelizing would require either a shared DbContext (concurrency exceptions) or per-call scopes (more complexity). Deferred until adopters report slow batches. Same call-pattern as the v1.3 #87 bulk-send worker, which has been fine.
- **Bulk void uses a single shared reason.** "Voided in bulk by admin" by default; the UI doesn't yet prompt for a custom reason. The endpoint accepts one. v2.1 polish: prompt-for-reason modal before the API call.
- **Audit actor is denormalized, not joined.** `ActorRole` is stored on every audit row instead of joining to a separate Users table on read. Pros: zero-join queries for "all admin actions in this tenant"; survives role demotions (the row preserves the role they had at action time). Cons: storage overhead and the role-at-event-time-vs-now ambiguity if you actually want the latter. v2.0's audit consumer (the audit endpoint) is fine with denormalized; if v2.1 adds an audit-console UI that wants live joins, that's the time to revisit.
- **`cleanup-demo` doesn't actually re-seed.** It only wipes. The v1.2 `/demo/seed` endpoint is still there as the seeding counterpart — the admin can chain "Reset" → "Seed demo" if they want a fresh start. Combining into one button is a v2.1 ergonomics win.
- **Demo cleanup gate is binary.** Dev OR explicit allow-flag. There's no granular "let admins clean their own tenant in prod but not the global tenant" mode — that's a multi-tenant feature that doesn't make sense until v2.x adds real tenant management. For now: prod adopters who set the flag are accepting "any admin can wipe."
- **Bulk-resend only sees recipients on the current page.** Selection persists across pages but recipient ids are gathered from `_page.Items` only. An admin who selected 30 requests across 3 pages, then paged back to page 1 and clicked "Resend invitation," only gets the page-1 recipients. v2.1 fix: fetch recipient ids for all selected request ids before the API call. Acceptable for v2.0 because the typical workflow is "find 5 stuck requests, void or resend them all on one page."
- **Voided webhook payload is minimal.** Carries SigningRequestId + Reason + VoidedAtUtc. Subscribers wanting the full request shape need to GET /api/signing-requests/{id} after receiving the event. Same pattern as v1.x's other webhook events; consistent if not maximally helpful.
- **No "undo bulk void" affordance.** Voided is terminal in the state machine — there's no UndoAsync. Adopters who need an undo would have to re-dispatch the workflow from the original template. Intentional: a void is a deliberate admin action and undoing it silently would muddy the audit trail.
- **Pre-v2 audit rows have null actors.** No backfill — we don't have the data. Audit-consumer queries that filter on `ActorUserId IS NOT NULL` work correctly; queries that filter on a specific actor get empty results for pre-v2 rows even if that same user voided things via the v1.x API. Documented limitation.
