using MbaLms.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using System.Data.Common;

namespace MbaLms.Api.Tests.Infrastructure;

/// <summary>
/// Test-only hook: when armed, waits until a business entity and ChangeLogEntry are staged together,
/// then forces a Postgres failure inside SaveChanges (not a pre-SQL throw).
/// For Survey/SurveyTemplate Update (two SaveChanges in one transaction), stays armed across the
/// intermediate question-delete save and records that it completed successfully.
/// Registered only in ApiFactory test host — never in the production API project.
/// </summary>
public sealed class LessonChangeLogSaveFailInterceptor : SaveChangesInterceptor, IDbCommandInterceptor
{
    private int _arm;
    private int _awaitingDbError;
    private int _pendingIntermediateConfirm;
    /// <summary>After Student+CL delete SaveChanges succeeds, fail the next Identity SaveChanges.</summary>
    private int _failNextAfterStudentDelete;

    /// <summary>True when a pending business mutation and ChangeLogEntry were seen together.</summary>
    public bool SawPendingLessonAndChangeLog { get; private set; }

    /// <summary>
    /// True when an intermediate SaveChanges (question deletes, no ChangeLog) completed successfully
    /// while armed — i.e. the first save of a Survey/Template Update rewrite.
    /// </summary>
    public bool SawSuccessfulIntermediateSaveChanges { get; private set; }

    /// <summary>How many SurveyQuestion / SurveyTemplateQuestion rows were deleted in that intermediate save.</summary>
    public int IntermediateDeletedQuestionCount { get; private set; }

    public bool SawDatabaseCommandFailure { get; private set; }
    public string? FailedSqlState { get; private set; }

    /// <summary>AppUser.Email observed on an Identity SaveChanges that completed while armed (pre-fail).</summary>
    public string? CapturedMidTxUserEmail { get; private set; }

    /// <summary>True when PasswordHash was modified on a mid-tx Identity SaveChanges (value not exposed).</summary>
    public bool CapturedMidTxPasswordHashModified { get; private set; }

    /// <summary>True when an AppUser insert was part of a successful mid-tx Identity SaveChanges.</summary>
    public bool SawIdentityUserInsert { get; private set; }

    /// <summary>True when an AspNetUserRoles insert was part of a successful mid-tx Identity SaveChanges.</summary>
    public bool SawIdentityRoleAssign { get; private set; }

    /// <summary>True when the failing SaveChanges included an AppUser delete (Identity DeleteAsync path).</summary>
    public bool SawIdentityUserDeleteOnFailingSave { get; private set; }

    public void ArmOnce()
    {
        SawPendingLessonAndChangeLog = false;
        SawSuccessfulIntermediateSaveChanges = false;
        IntermediateDeletedQuestionCount = 0;
        SawDatabaseCommandFailure = false;
        FailedSqlState = null;
        CapturedMidTxUserEmail = null;
        CapturedMidTxPasswordHashModified = false;
        SawIdentityUserInsert = false;
        SawIdentityRoleAssign = false;
        SawIdentityUserDeleteOnFailingSave = false;
        Interlocked.Exchange(ref _awaitingDbError, 0);
        Interlocked.Exchange(ref _pendingIntermediateConfirm, 0);
        Interlocked.Exchange(ref _failNextAfterStudentDelete, 0);
        Interlocked.Exchange(ref _arm, 1);
    }

    public void Disarm()
    {
        Interlocked.Exchange(ref _arm, 0);
        Interlocked.Exchange(ref _awaitingDbError, 0);
        Interlocked.Exchange(ref _pendingIntermediateConfirm, 0);
        Interlocked.Exchange(ref _failNextAfterStudentDelete, 0);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        CorruptIfArmed(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        CorruptIfArmed(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ConfirmIntermediateSuccess(result);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ConfirmIntermediateSuccess(result);
        return ValueTask.FromResult(result);
    }

    private void ConfirmIntermediateSuccess(int result)
    {
        if (Interlocked.Exchange(ref _pendingIntermediateConfirm, 0) != 1) return;
        if (result > 0)
            SawSuccessfulIntermediateSaveChanges = true;
    }

    private void CorruptIfArmed(DbContext? db)
    {
        if (db is null || Volatile.Read(ref _arm) != 1) return;

        var logs = db.ChangeTracker.Entries<ChangeLogEntry>()
            .Where(e => e.State == EntityState.Added)
            .ToList();
        var lessons = db.ChangeTracker.Entries<Lesson>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        var surveys = db.ChangeTracker.Entries<Survey>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        var templates = db.ChangeTracker.Entries<SurveyTemplate>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        var grades = db.ChangeTracker.Entries<Grade>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        var groups = db.ChangeTracker.Entries<Group>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        var disciplines = db.ChangeTracker.Entries<Discipline>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        var periods = db.ChangeTracker.Entries<AcademicPeriod>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        var teachers = db.ChangeTracker.Entries<Teacher>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        var students = db.ChangeTracker.Entries<Student>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        var appUsers = db.ChangeTracker.Entries<AppUser>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        var userRoles = db.ChangeTracker.Entries<IdentityUserRole<Guid>>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        // Student delete: let Student+CL SaveChanges succeed, then fail Identity DeleteAsync SaveChanges.
        if (Volatile.Read(ref _failNextAfterStudentDelete) == 1)
        {
            Interlocked.Exchange(ref _arm, 0);
            Interlocked.Exchange(ref _failNextAfterStudentDelete, 0);
            Interlocked.Exchange(ref _pendingIntermediateConfirm, 0);
            SawPendingLessonAndChangeLog = true;
            SawIdentityUserDeleteOnFailingSave = appUsers.Exists(e => e.State == EntityState.Deleted);
            Interlocked.Exchange(ref _awaitingDbError, 1);
            db.Set<ChangeLogEntry>().Add(new ChangeLogEntry
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTimeOffset.UtcNow,
                Action = ChangeLogAction.Deleted,
                EntityType = ChangeLogEntityType.Student,
                EntityId = Guid.CreateVersion7(),
                ChangesJson = "not-valid-json",
                ContextJson = "{}",
            });
            return;
        }

        if (students.Exists(e => e.State == EntityState.Deleted)
            && logs.Exists(e => e.Entity.EntityType == ChangeLogEntityType.Student))
        {
            SawPendingLessonAndChangeLog = true;
            Interlocked.Exchange(ref _failNextAfterStudentDelete, 1);
            Interlocked.Exchange(ref _pendingIntermediateConfirm, 1);
            return; // stay armed for Identity DeleteAsync
        }

        // Password-only student update: Student may stay Unchanged while only ChangeLog is Added.
        var studentOnlyLogs = logs.Count > 0
            && lessons.Count == 0 && surveys.Count == 0 && templates.Count == 0 && grades.Count == 0
            && groups.Count == 0 && disciplines.Count == 0 && periods.Count == 0 && teachers.Count == 0
            && students.Count == 0
            && logs.Exists(e => e.Entity.EntityType == ChangeLogEntityType.Student);

        // Stay armed across intermediate SaveChanges (survey/template question rewrite / Identity) until both sides are present.
        if (!studentOnlyLogs && (logs.Count == 0 || (lessons.Count == 0 && surveys.Count == 0 && templates.Count == 0 && grades.Count == 0
            && groups.Count == 0 && disciplines.Count == 0 && periods.Count == 0 && teachers.Count == 0
            && students.Count == 0)))
        {
            if (logs.Count == 0 && lessons.Count == 0 && surveys.Count == 0 && templates.Count == 0 && grades.Count == 0
                && groups.Count == 0 && disciplines.Count == 0 && periods.Count == 0 && teachers.Count == 0
                && students.Count == 0)
            {
                var deletedQs = db.ChangeTracker.Entries<SurveyQuestion>()
                    .Count(e => e.State == EntityState.Deleted)
                    + db.ChangeTracker.Entries<SurveyTemplateQuestion>()
                        .Count(e => e.State == EntityState.Deleted);
                if (deletedQs > 0)
                {
                    IntermediateDeletedQuestionCount = deletedQs;
                    Interlocked.Exchange(ref _pendingIntermediateConfirm, 1);
                }
                else if (appUsers.Count > 0 || userRoles.Count > 0)
                {
                    // Identity Create/Update/password/role SaveChanges before Student+CL final write.
                    CaptureMidTxIdentity(appUsers, userRoles);
                    Interlocked.Exchange(ref _pendingIntermediateConfirm, 1);
                }
            }
            return;
        }

        Interlocked.Exchange(ref _arm, 0);
        Interlocked.Exchange(ref _pendingIntermediateConfirm, 0);
        SawPendingLessonAndChangeLog = true;
        Interlocked.Exchange(ref _awaitingDbError, 1);

        if (lessons.Exists(e => e.State is EntityState.Added or EntityState.Modified))
        {
            foreach (var entry in lessons.Where(e => e.State is EntityState.Added or EntityState.Modified))
                entry.Entity.EndsAt = entry.Entity.StartsAt; // CK_Lessons_EndsAfterStart
            return;
        }

        if (surveys.Exists(e => e.State is EntityState.Added or EntityState.Modified))
        {
            foreach (var entry in surveys.Where(e => e.State is EntityState.Added or EntityState.Modified))
                entry.Entity.GroupId = Guid.Empty; // FK violation
            return;
        }

        if (grades.Exists(e => e.State is EntityState.Added or EntityState.Modified))
        {
            foreach (var entry in grades.Where(e => e.State is EntityState.Added or EntityState.Modified))
                entry.Entity.Value = -1; // CK value 0..100
            return;
        }

        if (groups.Exists(e => e.State is EntityState.Added or EntityState.Modified))
        {
            foreach (var entry in groups.Where(e => e.State is EntityState.Added or EntityState.Modified))
            {
                entry.Entity.StartDate = new DateOnly(2030, 6, 1);
                entry.Entity.EndDate = new DateOnly(2030, 1, 1); // CK_Groups_EndNotBeforeStart
            }
            return;
        }

        if (periods.Exists(e => e.State is EntityState.Added or EntityState.Modified))
        {
            foreach (var entry in periods.Where(e => e.State is EntityState.Added or EntityState.Modified))
            {
                entry.Entity.StartDate = new DateOnly(2030, 6, 1);
                entry.Entity.EndDate = new DateOnly(2030, 1, 1); // CK_Periods_EndNotBeforeStart
            }
            return;
        }

        // Deletes / templates / disciplines / students: poison ChangeLog jsonb so Postgres rejects during SaveChanges.
        foreach (var entry in logs)
            entry.Entity.ChangesJson = "not-valid-json";
    }

    private void CaptureMidTxIdentity(
        List<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<AppUser>> appUsers,
        List<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<IdentityUserRole<Guid>>> userRoles)
    {
        foreach (var entry in appUsers)
        {
            if (entry.State == EntityState.Added)
                SawIdentityUserInsert = true;
            if (entry.State is EntityState.Added or EntityState.Modified)
                CapturedMidTxUserEmail = entry.Entity.Email;
            if (entry.State == EntityState.Modified && entry.Property(u => u.PasswordHash).IsModified)
                CapturedMidTxPasswordHashModified = true;
        }
        if (userRoles.Exists(e => e.State == EntityState.Added))
            SawIdentityRoleAssign = true;
    }

    public void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        if (Interlocked.Exchange(ref _awaitingDbError, 0) != 1) return;
        SawDatabaseCommandFailure = true;
        FailedSqlState = eventData.Exception switch
        {
            PostgresException pg => pg.SqlState,
            DbUpdateException { InnerException: PostgresException pg } => pg.SqlState,
            _ => eventData.Exception.GetType().Name
        };
    }

    public Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        CommandFailed(command, eventData);
        return Task.CompletedTask;
    }
}
