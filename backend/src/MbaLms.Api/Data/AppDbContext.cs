using MbaLms.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<MbaProgram> Programs => Set<MbaProgram>();
    public DbSet<AcademicPeriod> Periods => Set<AcademicPeriod>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<Teacher> Teachers => Set<Teacher>();
    public DbSet<Discipline> Disciplines => Set<Discipline>();
    public DbSet<DisciplineProgram> DisciplinePrograms => Set<DisciplineProgram>();
    public DbSet<AssessmentCategory> AssessmentCategories => Set<AssessmentCategory>();
    public DbSet<AssessmentElement> AssessmentElements => Set<AssessmentElement>();
    public DbSet<DisciplineAssessmentSettings> DisciplineAssessmentSettings => Set<DisciplineAssessmentSettings>();
    public DbSet<WorkSheet> WorkSheets => Set<WorkSheet>();
    public DbSet<WorkSheetRow> WorkSheetRows => Set<WorkSheetRow>();
    public DbSet<WorkSheetScore> WorkSheetScores => Set<WorkSheetScore>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<Grade> Grades => Set<Grade>();
    public DbSet<GradeHistoryEntry> GradeHistory => Set<GradeHistoryEntry>();
    public DbSet<Survey> Surveys => Set<Survey>();
    public DbSet<SurveyQuestion> SurveyQuestions => Set<SurveyQuestion>();
    public DbSet<SurveyQuestionOption> SurveyQuestionOptions => Set<SurveyQuestionOption>();
    public DbSet<SurveyResponse> SurveyResponses => Set<SurveyResponse>();
    public DbSet<SurveyAnswer> SurveyAnswers => Set<SurveyAnswer>();
    public DbSet<SurveyTemplate> SurveyTemplates => Set<SurveyTemplate>();
    public DbSet<SurveyTemplateQuestion> SurveyTemplateQuestions => Set<SurveyTemplateQuestion>();
    public DbSet<SurveyTemplateOption> SurveyTemplateOptions => Set<SurveyTemplateOption>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<AnnouncementGroup> AnnouncementGroups => Set<AnnouncementGroup>();
    public DbSet<ChangeLogEntry> ChangeLogEntries => Set<ChangeLogEntry>();
    public DbSet<AdminAuditEntry> AdminAuditEntries => Set<AdminAuditEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Academic records are never cascade-deleted: archiving is a status change,
        // and reference data in use cannot be removed (Restrict).
        b.Entity<MbaProgram>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
        });

        b.Entity<AcademicPeriod>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_Periods_EndNotBeforeStart",
                "\"StartDate\" IS NULL OR \"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\""));
        });

        b.Entity<Group>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.Program).WithMany().HasForeignKey(x => x.ProgramId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Groups_EndNotBeforeStart",
                "\"StartDate\" IS NULL OR \"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\""));
        });

        b.Entity<Student>(e =>
        {
            e.Ignore(x => x.FullName);
            e.Property(x => x.LastName).HasMaxLength(100);
            e.Property(x => x.FirstName).HasMaxLength(100);
            e.Property(x => x.MiddleName).HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(256);
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Group).WithMany(g => g.Students).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Teacher>(e =>
        {
            e.Ignore(x => x.FullName);
            e.Property(x => x.LastName).HasMaxLength(100);
            e.Property(x => x.FirstName).HasMaxLength(100);
            e.Property(x => x.MiddleName).HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(256);
        });

        b.Entity<Discipline>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<DisciplineProgram>(e =>
        {
            e.HasKey(x => x.DisciplineId);
            e.Property(x => x.FileName).HasMaxLength(255);
            // Two parallel replacements must not both win: the loser would leave its file orphaned.
            e.Property(x => x.StorageKey).IsConcurrencyToken();
            e.HasIndex(x => x.StorageKey).IsUnique();
            e.HasOne(x => x.Discipline).WithOne().HasForeignKey<DisciplineProgram>(x => x.DisciplineId)
                .OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_DisciplinePrograms_SizePositive", "\"SizeBytes\" > 0"));
        });

        b.Entity<AssessmentCategory>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Weight).HasPrecision(5, 2);
            e.Property(x => x.IncludeInAverage).HasDefaultValue(true);
            e.HasIndex(x => new { x.DisciplineId, x.Order });
            e.HasIndex(x => new { x.DisciplineId, x.Kind }).IsUnique()
                .HasFilter("\"Kind\" = 'Attendance'");
            e.HasOne(x => x.Discipline).WithMany().HasForeignKey(x => x.DisciplineId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_AssessmentCategories_WeightRange", "\"Weight\" >= 0 AND \"Weight\" <= 100"));
        });

        b.Entity<AssessmentElement>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => new { x.CategoryId, x.Order });
            e.HasOne(x => x.Category).WithMany(c => c.Works).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DisciplineAssessmentSettings>(e =>
        {
            e.HasKey(x => x.DisciplineId);
            e.Property(x => x.AttendanceThreshold).HasPrecision(5, 2);
            e.Property(x => x.CalculationMode).HasDefaultValue(CalculationMode.Weighted);
            e.Property(x => x.IncludeAttendanceInAverage).HasDefaultValue(false);
            e.HasOne(x => x.Discipline).WithOne().HasForeignKey<DisciplineAssessmentSettings>(x => x.DisciplineId)
                .OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_DisciplineAssessmentSettings_ThresholdRange",
                "\"AttendanceThreshold\" >= 0 AND \"AttendanceThreshold\" <= 100"));
        });

        b.Entity<WorkSheet>(e =>
        {
            e.HasIndex(x => new { x.GroupId, x.DisciplineId, x.PeriodId }).IsUnique();
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Discipline).WithMany().HasForeignKey(x => x.DisciplineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Period).WithMany().HasForeignKey(x => x.PeriodId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_WorkSheets_TotalLessons", "\"TotalLessons\" >= 0"));
        });

        b.Entity<WorkSheetRow>(e =>
        {
            e.HasIndex(x => new { x.WorkSheetId, x.StudentId }).IsUnique();
            e.HasIndex(x => x.StudentId);
            e.Property(x => x.CalculatedRaw).HasPrecision(12, 6);
            e.HasOne(x => x.WorkSheet).WithMany(x => x.Rows).HasForeignKey(x => x.WorkSheetId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_WorkSheetRows_Attended", "\"AttendedLessons\" >= 0");
                t.HasCheckConstraint("CK_WorkSheetRows_ManualFinal", "\"ManualFinal\" >= 0 AND \"ManualFinal\" <= 10");
                t.HasCheckConstraint("CK_WorkSheetRows_CalculatedRounded", "\"CalculatedRounded\" >= 0 AND \"CalculatedRounded\" <= 10");
            });
        });

        b.Entity<WorkSheetScore>(e =>
        {
            e.HasKey(x => new { x.RowId, x.ElementId });
            e.HasIndex(x => x.ElementId);
            e.Property(x => x.Value).HasPrecision(4, 2);
            e.HasOne(x => x.Row).WithMany(x => x.Scores).HasForeignKey(x => x.RowId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Element).WithMany().HasForeignKey(x => x.ElementId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_WorkSheetScores_ValueRange", "\"Value\" >= 0 AND \"Value\" <= 10"));
        });

        b.Entity<Lesson>(e =>
        {
            e.Property(x => x.Location).HasMaxLength(500);
            e.Property(x => x.Comment).HasMaxLength(2000);
            e.HasIndex(x => new { x.GroupId, x.StartsAt });
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Discipline).WithMany().HasForeignKey(x => x.DisciplineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TeacherId, x.StartsAt });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Lessons_EndsAfterStart", "\"EndsAt\" > \"StartsAt\"");
                t.HasCheckConstraint("CK_Lessons_Status", "\"Status\" IN ('Scheduled', 'Cancelled')");
            });
        });

        b.Entity<Grade>(e =>
        {
            e.HasIndex(x => new { x.StudentId, x.DisciplineId, x.PeriodId }).IsUnique();
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Discipline).WithMany().HasForeignKey(x => x.DisciplineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Period).WithMany().HasForeignKey(x => x.PeriodId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Grades_ValueRange", "\"Value\" >= 0 AND \"Value\" <= 100"));
        });

        b.Entity<GradeHistoryEntry>(e =>
        {
            e.ToTable("GradeHistory");
            e.HasIndex(x => new { x.StudentId, x.DisciplineId, x.PeriodId, x.ChangedAt });
            e.HasIndex(x => x.GradeId);
            e.HasOne(x => x.ChangedBy).WithMany().HasForeignKey(x => x.ChangedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Survey>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.HasIndex(x => new { x.GroupId, x.Status });
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Discipline).WithMany().HasForeignKey(x => x.DisciplineId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Questions).WithOne(q => q.Survey).HasForeignKey(q => q.SurveyId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Responses).WithOne(r => r.Survey).HasForeignKey(r => r.SurveyId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Surveys_ClosesAfterOpens",
                "\"OpensAt\" IS NULL OR \"ClosesAt\" IS NULL OR \"ClosesAt\" > \"OpensAt\""));
        });

        b.Entity<SurveyQuestion>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(1000);
            e.Property(x => x.ScaleMinLabel).HasMaxLength(100);
            e.Property(x => x.ScaleMaxLabel).HasMaxLength(100);
            e.HasMany(x => x.Options).WithOne(o => o.Question).HasForeignKey(o => o.QuestionId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_SurveyQuestions_ScaleRange",
                "\"ScaleMin\" IS NULL OR \"ScaleMax\" IS NULL OR \"ScaleMin\" < \"ScaleMax\""));
        });

        b.Entity<SurveyQuestionOption>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(500);
        });

        b.Entity<SurveyTemplate>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.HasIndex(x => x.CreatedAt);
            e.HasMany(x => x.Questions).WithOne(q => q.Template).HasForeignKey(q => q.TemplateId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SurveyTemplateQuestion>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(1000);
            e.Property(x => x.ScaleMinLabel).HasMaxLength(100);
            e.Property(x => x.ScaleMaxLabel).HasMaxLength(100);
            e.HasMany(x => x.Options).WithOne(o => o.Question).HasForeignKey(o => o.QuestionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_SurveyTemplateQuestions_ScaleRange",
                "\"ScaleMin\" IS NULL OR \"ScaleMax\" IS NULL OR \"ScaleMin\" < \"ScaleMax\""));
        });

        b.Entity<SurveyTemplateOption>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(500);
        });

        b.Entity<SurveyResponse>(e =>
        {
            e.HasIndex(x => new { x.SurveyId, x.StudentId }).IsUnique();
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Answers).WithOne(a => a.Response).HasForeignKey(a => a.ResponseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SurveyAnswer>(e =>
        {
            e.Property(x => x.TextValue).HasMaxLength(4000);
            e.HasIndex(x => new { x.ResponseId, x.QuestionId }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_SurveyAnswers_SingleValue",
                "num_nonnulls(\"IntValue\", \"OptionId\", \"TextValue\") <= 1"));
            e.HasOne(x => x.Question).WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Option).WithMany().HasForeignKey(x => x.OptionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Notification>(e =>
        {
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Announcement>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Body).HasMaxLength(8000);
            e.HasIndex(x => new { x.Status, x.PublishedAt });
            e.HasIndex(x => x.IsPinned);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Groups).WithOne(g => g.Announcement).HasForeignKey(g => g.AnnouncementId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AnnouncementGroup>(e =>
        {
            e.HasKey(x => new { x.AnnouncementId, x.GroupId });
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.GroupId);
        });

        b.Entity<AdminAuditEntry>(e =>
        {
            e.ToTable("AdminAuditEntries");
            e.Property(x => x.ActorEmail).HasMaxLength(256);
            e.Property(x => x.TargetEmail).HasMaxLength(256);
            e.Property(x => x.TargetKind).HasMaxLength(32);
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.OccurredAt, x.Id });
            e.HasIndex(x => x.TargetKind);
            e.HasIndex(x => x.ActorUserId);
            e.HasIndex(x => x.TargetUserId);
        });

        b.Entity<ChangeLogEntry>(e =>
        {
            e.ToTable("ChangeLogEntries");
            e.Property(x => x.ChangesJson).HasColumnType("jsonb");
            e.Property(x => x.ContextJson).HasColumnType("jsonb");
            // No FK on EntityId (history survives deletes). No FK on ActorUserId (deleted user ≠ system).
            e.HasIndex(x => new { x.OccurredAt, x.Id });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.ActorUserId);
            e.HasIndex(x => x.Action);
        });
    }
}
