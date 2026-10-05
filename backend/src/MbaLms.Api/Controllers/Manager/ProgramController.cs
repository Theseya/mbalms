using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

public record ProgramDto(Guid Id, string Name);

public record ProgramRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(200, ErrorMessage = FieldCodes.MaxLength)] string Name);

/// <summary>The single MBA programme: it is created by the seeder and can only be renamed.</summary>
[Route("api/manager/program")]
public class ProgramController(AppDbContext db) : ManagerControllerBase(db)
{
    [HttpGet]
    public async Task<ProgramDto> Get(CancellationToken ct) =>
        await Db.Programs.AsNoTracking().Select(p => new ProgramDto(p.Id, p.Name)).FirstOrDefaultAsync(ct)
        ?? throw AppException.NotFound();

    [HttpPut]
    public async Task<ProgramDto> Update(ProgramRequest request, CancellationToken ct)
    {
        var program = await Db.Programs.FirstOrDefaultAsync(ct) ?? throw AppException.NotFound();
        program.Name = request.Name.Trim();
        await Db.SaveChangesAsync(ct);
        return new ProgramDto(program.Id, program.Name);
    }
}
