using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

public record StudentDto(Guid Id, string LastName, string FirstName, string? MiddleName, string FullName, string Email,
    Guid GroupId, string GroupName, GroupStatus GroupStatus);

public record StudentRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(100, ErrorMessage = FieldCodes.MaxLength)] string LastName,
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(100, ErrorMessage = FieldCodes.MaxLength)] string FirstName,
    [MaxLength(100, ErrorMessage = FieldCodes.MaxLength)] string? MiddleName,
    [Required(ErrorMessage = FieldCodes.Required)] [EmailAddress(ErrorMessage = FieldCodes.Email)] [MaxLength(256, ErrorMessage = FieldCodes.MaxLength)] string Email,
    [Required(ErrorMessage = FieldCodes.Required)] Guid? GroupId,
    // Required on create; on update, a non-empty value resets the password.
    [MaxLength(256, ErrorMessage = FieldCodes.MaxLength)] string? Password);

/// <param name="DevelopmentPreviewId">Set only when Email:Provider=Development (local capture). Never includes the token.</param>
public record StudentInviteResultDto(bool Queued, Guid? DevelopmentPreviewId);

[Route("api/manager/students")]
public class StudentsController(
    AppDbContext db, UserManager<AppUser> users,
    ChangeLogWriter changeLog, CurrentUser current,
    IEmailSender emailSender, IEmailOutbox outbox,
    PasswordSetupLinkService passwordLinks,
    IHostEnvironment env) : ManagerControllerBase(db)
{
    /// <summary>Students of active groups by default; a group filter also shows archived groups.</summary>
    [HttpGet]
    public async Task<PagedResult<StudentDto>> List(
        [FromQuery] Guid? groupId,
        [FromQuery] bool includeArchived = false,
        [FromQuery] [MaxLength(Paging.MaxSearchLength, ErrorMessage = FieldCodes.MaxLength)] string? search = null,
        [FromQuery] [Range(1, int.MaxValue, ErrorMessage = FieldCodes.Range)] int page = 1,
        [FromQuery] [Range(1, Paging.MaxPageSize, ErrorMessage = FieldCodes.Range)] int pageSize = Paging.DefaultPageSize,
        CancellationToken ct = default)
    {
        var q = Filter(Db.Students.AsNoTracking(), groupId, includeArchived, search);
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(s => s.LastName).ThenBy(s => s.FirstName).ThenBy(s => s.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(s => new StudentDto(s.Id, s.LastName, s.FirstName, s.MiddleName,
                s.MiddleName == null ? s.LastName + " " + s.FirstName : s.LastName + " " + s.FirstName + " " + s.MiddleName,
                s.Email, s.GroupId, s.Group!.Name, s.Group.Status))
            .ToListAsync(ct);
        return new PagedResult<StudentDto>(items, total, page, pageSize);
    }

    /// <summary>Shared by the list and the Excel export. Every search word must match a name part or the email.</summary>
    internal static IQueryable<Student> Filter(IQueryable<Student> q, Guid? groupId, bool includeArchived, string? search)
    {
        if (groupId is not null) q = q.Where(s => s.GroupId == groupId);
        else if (!includeArchived) q = q.Where(s => s.Group!.Status == GroupStatus.Active);
        foreach (var term in (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = ContainsPattern(term);
            q = q.Where(s => EF.Functions.ILike(s.LastName, p) || EF.Functions.ILike(s.FirstName, p)
                             || (s.MiddleName != null && EF.Functions.ILike(s.MiddleName, p)) || EF.Functions.ILike(s.Email, p));
        }
        return q;
    }

    [HttpGet("{id:guid}")]
    public async Task<StudentDto> Get(Guid id, CancellationToken ct)
    {
        var s = await Db.Students.AsNoTracking().Include(x => x.Group).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw AppException.NotFound();
        return ToDto(s);
    }

    [HttpPost]
    public async Task<ActionResult<StudentDto>> Create(StudentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Password))
            throw AppException.Validation(nameof(request.Password), FieldCodes.Required);
        var group = await RequireActiveGroupAsync(request.GroupId, nameof(request.GroupId), ct);
        var email = request.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
            throw AppException.Validation(nameof(request.Email), ErrorCodes.Duplicate);

        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        var user = new AppUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded) throw IdentityFailure(result);
        result = await users.AddToRoleAsync(user, Roles.Student);
        if (!result.Succeeded) throw IdentityFailure(result);

        var student = new Student
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            LastName = request.LastName.Trim(),
            FirstName = request.FirstName.Trim(),
            MiddleName = Clean(request.MiddleName),
            Email = email,
            GroupId = request.GroupId!.Value
        };
        // Stage Student + ChangeLog only after Identity SaveChanges so they are not flushed early.
        var snap = StudentChangeLog.From(student, group.Name);
        changeLog.Add(
            ChangeLogEntityType.Student, student.Id, ChangeLogAction.Created,
            StudentChangeLog.ForCreate(snap, passwordSet: true),
            StudentChangeLog.Context(snap, await ActorEmailAsync()));
        Db.Students.Add(student);
        await Db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = student.Id }, await Get(student.Id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<StudentDto> Update(Guid id, StudentRequest request, CancellationToken ct)
    {
        var student = await Db.Students.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
        var beforeGroupName = await Db.Groups.AsNoTracking()
            .Where(g => g.Id == student.GroupId).Select(g => g.Name).FirstAsync(ct);
        if (student.GroupId != request.GroupId)
            await RequireActiveGroupAsync(request.GroupId, nameof(request.GroupId), ct);

        var user = await users.FindByIdAsync(student.UserId.ToString()) ?? throw AppException.NotFound();
        var email = request.Email.Trim();
        var before = StudentChangeLog.From(student, beforeGroupName);

        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            var other = await users.FindByEmailAsync(email);
            if (other is not null && other.Id != user.Id)
                throw AppException.Validation(nameof(request.Email), ErrorCodes.Duplicate);
            user.Email = email;
            user.UserName = email;
            var r = await users.UpdateAsync(user);
            if (!r.Succeeded) throw IdentityFailure(r);
        }

        var passwordReset = false;
        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            var r = await users.RemovePasswordAsync(user);
            if (r.Succeeded) r = await users.AddPasswordAsync(user, request.Password);
            if (!r.Succeeded) throw IdentityFailure(r);
            passwordReset = true;
        }

        // Mutate Student only after Identity SaveChanges so UserManager does not flush profile Diff early.
        student.LastName = request.LastName.Trim();
        student.FirstName = request.FirstName.Trim();
        student.MiddleName = Clean(request.MiddleName);
        student.Email = email;
        student.GroupId = request.GroupId!.Value;

        var afterGroupName = await Db.Groups.AsNoTracking()
            .Where(g => g.Id == student.GroupId).Select(g => g.Name).FirstAsync(ct);
        var after = StudentChangeLog.From(student, afterGroupName);
        var changes = StudentChangeLog.Diff(before, after, passwordReset);
        if (changes.Count > 0)
        {
            changeLog.Add(
                ChangeLogEntityType.Student, student.Id, ChangeLogAction.Updated,
                changes, StudentChangeLog.Context(after, await ActorEmailAsync()));
        }

        await Db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await Get(id, ct);
    }

    /// <summary>
    /// Prepares an invite email with login (email) and a one-time password-setup link (no password in the message).
    /// Delivery is delegated to <see cref="IEmailSender"/>; Development captures for preview, Production without SMTP fails clearly.
    /// </summary>
    [HttpPost("{id:guid}/invite")]
    [EnableRateLimiting("password-mail")]
    public async Task<StudentInviteResultDto> Invite(Guid id, CancellationToken ct)
    {
        if (!emailSender.IsConfigured)
            throw AppException.ServiceUnavailable(ErrorCodes.MailNotConfigured);

        var student = await Db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw AppException.NotFound();
        var user = await users.FindByIdAsync(student.UserId.ToString())
            ?? throw AppException.NotFound();

        var (_, setupUrl) = await passwordLinks.CreateResetLinkAsync(user);
        var message = PasswordEmailComposer.Invite(
            student.Email,
            $"{student.FirstName} {student.LastName}".Trim(),
            setupUrl);
        await emailSender.SendAsync(message, ct);

        Guid? previewId = null;
        if (env.IsDevelopment() || env.IsEnvironment("Testing"))
            previewId = outbox.LatestFor(student.Email)?.Id;

        return new StudentInviteResultDto(Queued: true, DevelopmentPreviewId: previewId);
    }

    /// <summary>Students with grades, working-sheet results or survey responses cannot be deleted (data is kept).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var student = await Db.Students.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
        if (await Db.Grades.AnyAsync(g => g.StudentId == id, ct) || await Db.SurveyResponses.AnyAsync(r => r.StudentId == id, ct)
            || await Db.WorkSheetRows.AnyAsync(r => r.StudentId == id, ct))
            throw AppException.Conflict(ErrorCodes.InUse);

        var groupName = await Db.Groups.AsNoTracking()
            .Where(g => g.Id == student.GroupId).Select(g => g.Name).FirstAsync(ct);
        var snap = StudentChangeLog.From(student, groupName);

        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        changeLog.Add(
            ChangeLogEntityType.Student, student.Id, ChangeLogAction.Deleted,
            StudentChangeLog.ForDelete(snap), StudentChangeLog.Context(snap, await ActorEmailAsync()));
        Db.Students.Remove(student);
        await Db.SaveChangesAsync(ct);
        var user = await users.FindByIdAsync(student.UserId.ToString());
        if (user is not null)
        {
            var r = await users.DeleteAsync(user);
            if (!r.Succeeded) throw IdentityFailure(r);
        }
        await tx.CommitAsync(ct);
        return NoContent();
    }

    private static StudentDto ToDto(Student s) =>
        new(s.Id, s.LastName, s.FirstName, s.MiddleName, s.FullName, s.Email, s.GroupId, s.Group!.Name, s.Group.Status);

    private async Task<string?> ActorEmailAsync()
    {
        var user = await users.FindByIdAsync(current.UserId.ToString());
        return user?.Email ?? user?.UserName;
    }

    private static AppException IdentityFailure(IdentityResult result)
    {
        var password = result.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal));
        if (password) return AppException.Validation("password", FieldCodes.PasswordWeak);
        var duplicate = result.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName");
        if (duplicate) return AppException.Validation("email", ErrorCodes.Duplicate);
        return AppException.Validation("email", FieldCodes.Invalid);
    }
}
