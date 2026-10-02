using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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

[Route("api/manager/students")]
public class StudentsController(AppDbContext db, UserManager<AppUser> users) : ManagerControllerBase(db)
{
    [HttpGet]
    public async Task<List<StudentDto>> List([FromQuery] Guid? groupId, [FromQuery] bool includeArchived = false,
        CancellationToken ct = default)
    {
        var q = Db.Students.AsNoTracking();
        if (groupId is not null) q = q.Where(s => s.GroupId == groupId);
        else if (!includeArchived) q = q.Where(s => s.Group!.Status == GroupStatus.Active);
        return await q.OrderBy(s => s.LastName).ThenBy(s => s.FirstName)
            .Select(s => new StudentDto(s.Id, s.LastName, s.FirstName, s.MiddleName,
                s.MiddleName == null ? s.LastName + " " + s.FirstName : s.LastName + " " + s.FirstName + " " + s.MiddleName,
                s.Email, s.GroupId, s.Group!.Name, s.Group.Status))
            .ToListAsync(ct);
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
        await RequireActiveGroupAsync(request.GroupId, nameof(request.GroupId), ct);
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
        Db.Students.Add(student);
        await Db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = student.Id }, await Get(student.Id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<StudentDto> Update(Guid id, StudentRequest request, CancellationToken ct)
    {
        var student = await Db.Students.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
        if (student.GroupId != request.GroupId)
            await RequireActiveGroupAsync(request.GroupId, nameof(request.GroupId), ct);

        var user = await users.FindByIdAsync(student.UserId.ToString()) ?? throw AppException.NotFound();
        var email = request.Email.Trim();

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
        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            var r = await users.RemovePasswordAsync(user);
            if (r.Succeeded) r = await users.AddPasswordAsync(user, request.Password);
            if (!r.Succeeded) throw IdentityFailure(r);
        }

        student.LastName = request.LastName.Trim();
        student.FirstName = request.FirstName.Trim();
        student.MiddleName = Clean(request.MiddleName);
        student.Email = email;
        student.GroupId = request.GroupId!.Value;
        await Db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await Get(id, ct);
    }

    /// <summary>Students with grades or survey responses cannot be deleted (data is kept).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var student = await Db.Students.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
        if (await Db.Grades.AnyAsync(g => g.StudentId == id, ct) || await Db.SurveyResponses.AnyAsync(r => r.StudentId == id, ct))
            throw AppException.Conflict(ErrorCodes.InUse);

        await using var tx = await Db.Database.BeginTransactionAsync(ct);
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

    private static AppException IdentityFailure(IdentityResult result)
    {
        var password = result.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal));
        if (password) return AppException.Validation("password", FieldCodes.PasswordWeak);
        var duplicate = result.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName");
        if (duplicate) return AppException.Validation("email", ErrorCodes.Duplicate);
        return AppException.Validation("email", FieldCodes.Invalid);
    }
}
