using System.Security.Claims;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Infrastructure;

public class CurrentUser(IHttpContextAccessor accessor, AppDbContext db)
{
    public Guid UserId
    {
        get
        {
            var raw = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out var id) ? id : throw new UnauthorizedAccessException();
        }
    }

    /// <summary>Student profile of the signed-in user. Ownership checks for student endpoints start here.</summary>
    public async Task<Student> GetStudentAsync(CancellationToken ct)
    {
        var userId = UserId;
        return await db.Students.AsNoTracking().Include(s => s.Group).FirstOrDefaultAsync(s => s.UserId == userId, ct)
               ?? throw new AppException(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden);
    }
}
