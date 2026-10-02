using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace MbaLms.Api.Tests;

/// <summary>Database-level guarantees, checked directly against PostgreSQL, bypassing API validation.</summary>
public class SchemaTests(ApiFactory factory) : TestBase(factory)
{
    private static readonly string[] AppTables =
    [
        "Programs", "Periods", "Groups", "Students", "Teachers", "Disciplines", "Lessons", "Grades", "GradeHistory",
        "Surveys", "SurveyQuestions", "SurveyQuestionOptions", "SurveyResponses", "SurveyAnswers", "Notifications",
        "AspNetUsers", "AspNetRoles", "AspNetUserRoles"
    ];

    private record Graph(Group Group, Student Student, Teacher Teacher, Discipline Discipline, AcademicPeriod Period);

    [Fact]
    public async Task Migrations_apply_to_an_empty_database_and_roll_back()
    {
        var builder = new NpgsqlConnectionStringBuilder(Factory.ConnectionString) { Database = $"empty_{Guid.NewGuid():N}" };
        await using (var admin = new NpgsqlConnection(Factory.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{builder.Database}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await using var db = Context(builder.ConnectionString);
            await db.Database.MigrateAsync();

            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges(), "The model has changes that are not captured in a migration.");
            Assert.Superset(new HashSet<string>(AppTables), new HashSet<string>(await TablesAsync(db)));
            Assert.Empty(await db.Programs.ToListAsync());

            await db.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
            Assert.Equal(["__EFMigrationsHistory"], await TablesAsync(db));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(Factory.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{builder.Database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Grade_value_outside_0_100_violates_check_constraint(int value)
    {
        await using var db = Context();
        var g = await SeedAsync(db);
        db.Grades.Add(NewGrade(g, value));
        Assert.Equal("CK_Grades_ValueRange", await SaveViolationAsync(db));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task Grade_boundary_values_are_stored(int value)
    {
        await using var db = Context();
        var g = await SeedAsync(db);
        db.Grades.Add(NewGrade(g, value));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Grade_is_unique_per_student_discipline_and_period()
    {
        await using var db = Context();
        var g = await SeedAsync(db);
        db.Grades.Add(NewGrade(g, 70));
        await db.SaveChangesAsync();
        db.Grades.Add(NewGrade(g, 80));
        Assert.Equal("IX_Grades_StudentId_DisciplineId_PeriodId", await SaveViolationAsync(db));
    }

    [Fact]
    public async Task Lesson_must_end_after_start()
    {
        await using var db = Context();
        var g = await SeedAsync(db);
        var at = DateTimeOffset.UtcNow;
        db.Lessons.Add(new Lesson
        {
            Id = Guid.CreateVersion7(), GroupId = g.Group.Id, DisciplineId = g.Discipline.Id, TeacherId = g.Teacher.Id,
            StartsAt = at, EndsAt = at
        });
        Assert.Equal("CK_Lessons_EndsAfterStart", await SaveViolationAsync(db));
    }

    [Fact]
    public async Task Group_and_period_end_dates_cannot_precede_start()
    {
        await using var db = Context();
        var programId = await db.Programs.Select(p => p.Id).SingleAsync();
        db.Groups.Add(new Group
        {
            Id = Guid.CreateVersion7(), ProgramId = programId, Name = Unique("G"),
            StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 8, 31)
        });
        Assert.Equal("CK_Groups_EndNotBeforeStart", await SaveViolationAsync(db));

        await using var db2 = Context();
        db2.Periods.Add(new AcademicPeriod
        {
            Id = Guid.CreateVersion7(), Name = Unique("P"), StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 8, 31)
        });
        Assert.Equal("CK_Periods_EndNotBeforeStart", await SaveViolationAsync(db2));
    }

    [Fact]
    public async Task Survey_window_and_scale_range_are_checked()
    {
        await using var db = Context();
        var g = await SeedAsync(db);
        var survey = NewSurvey(g);
        survey.OpensAt = DateTimeOffset.UtcNow;
        survey.ClosesAt = survey.OpensAt;
        db.Surveys.Add(survey);
        Assert.Equal("CK_Surveys_ClosesAfterOpens", await SaveViolationAsync(db));

        await using var db2 = Context();
        var valid = NewSurvey(g);
        valid.Questions.Add(new SurveyQuestion
        {
            Id = Guid.CreateVersion7(), Text = "Q", Type = QuestionType.Scale, ScaleMin = 5, ScaleMax = 5
        });
        db2.Surveys.Add(valid);
        Assert.Equal("CK_SurveyQuestions_ScaleRange", await SaveViolationAsync(db2));
    }

    [Fact]
    public async Task Response_belongs_to_an_existing_student_and_is_unique_per_survey()
    {
        await using var db = Context();
        var g = await SeedAsync(db);
        var survey = NewSurvey(g);
        db.Surveys.Add(survey);
        db.SurveyResponses.Add(NewResponse(survey.Id, g.Student.Id));
        await db.SaveChangesAsync();

        db.SurveyResponses.Add(NewResponse(survey.Id, g.Student.Id));
        Assert.Equal("IX_SurveyResponses_SurveyId_StudentId", await SaveViolationAsync(db));

        await using var db2 = Context();
        db2.SurveyResponses.Add(NewResponse(survey.Id, Guid.CreateVersion7()));
        Assert.Equal("FK_SurveyResponses_Students_StudentId", await SaveViolationAsync(db2));
    }

    [Fact]
    public async Task Answer_is_unique_per_question_and_holds_one_value()
    {
        await using var db = Context();
        var g = await SeedAsync(db);
        var survey = NewSurvey(g);
        var question = new SurveyQuestion { Id = Guid.CreateVersion7(), Text = "Q", Type = QuestionType.Text };
        survey.Questions.Add(question);
        var response = NewResponse(survey.Id, g.Student.Id);
        db.Surveys.Add(survey);
        db.SurveyResponses.Add(response);
        db.SurveyAnswers.Add(NewAnswer(response.Id, question.Id, text: "A"));
        await db.SaveChangesAsync();

        db.SurveyAnswers.Add(NewAnswer(response.Id, question.Id, text: "B"));
        Assert.Equal("IX_SurveyAnswers_ResponseId_QuestionId", await SaveViolationAsync(db));

        await using var db2 = Context();
        var other = new SurveyQuestion { Id = Guid.CreateVersion7(), SurveyId = survey.Id, Text = "Q2", Type = QuestionType.Scale, ScaleMin = 1, ScaleMax = 5 };
        db2.SurveyQuestions.Add(other);
        db2.SurveyAnswers.Add(NewAnswer(response.Id, other.Id, text: "x", intValue: 3));
        Assert.Equal("CK_SurveyAnswers_SingleValue", await SaveViolationAsync(db2));
    }

    [Fact]
    public async Task Only_one_programme_can_exist()
    {
        await using var db = Context();
        db.Programs.Add(new MbaProgram { Id = Guid.CreateVersion7(), Name = "Second" });
        Assert.Equal("IX_Programs_Single", await SaveViolationAsync(db));
    }

    [Fact]
    public async Task Archiving_a_group_keeps_its_records_and_the_group_cannot_be_deleted_while_used()
    {
        await using var db = Context();
        var g = await SeedAsync(db);
        db.Grades.Add(NewGrade(g, 90));
        db.Surveys.Add(NewSurvey(g));
        await db.SaveChangesAsync();

        await db.Groups.Where(x => x.Id == g.Group.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, GroupStatus.Archived));

        await using var check = Context();
        Assert.Equal(1, await check.Students.CountAsync(s => s.GroupId == g.Group.Id));
        Assert.Equal(1, await check.Grades.CountAsync(x => x.StudentId == g.Student.Id));
        Assert.Equal(1, await check.Surveys.CountAsync(s => s.GroupId == g.Group.Id));

        Assert.Matches("^FK_.+_Groups_GroupId$",
            await ExecViolationAsync(() => check.Groups.Where(x => x.Id == g.Group.Id).ExecuteDeleteAsync()));
    }

    [Fact]
    public async Task Referenced_records_are_protected_from_deletion()
    {
        await using var db = Context();
        var g = await SeedAsync(db);
        db.Grades.Add(NewGrade(g, 75));
        db.Lessons.Add(new Lesson
        {
            Id = Guid.CreateVersion7(), GroupId = g.Group.Id, DisciplineId = g.Discipline.Id, TeacherId = g.Teacher.Id,
            StartsAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        await db.SaveChangesAsync();

        Assert.Equal("FK_Grades_Students_StudentId",
            await ExecViolationAsync(() => db.Students.Where(x => x.Id == g.Student.Id).ExecuteDeleteAsync()));
        Assert.Equal("FK_Lessons_Teachers_TeacherId",
            await ExecViolationAsync(() => db.Teachers.Where(x => x.Id == g.Teacher.Id).ExecuteDeleteAsync()));
        Assert.Matches("^FK_.+_Disciplines_DisciplineId$",
            await ExecViolationAsync(() => db.Disciplines.Where(x => x.Id == g.Discipline.Id).ExecuteDeleteAsync()));
        Assert.Equal("FK_Grades_Periods_PeriodId",
            await ExecViolationAsync(() => db.Periods.Where(x => x.Id == g.Period.Id).ExecuteDeleteAsync()));
    }

    [Fact]
    public async Task Survey_questions_cascade_but_answered_surveys_are_protected()
    {
        await using var db = Context();
        var g = await SeedAsync(db);
        var draft = NewSurvey(g);
        var question = new SurveyQuestion { Id = Guid.CreateVersion7(), Text = "Q", Type = QuestionType.SingleChoice };
        question.Options.Add(new SurveyQuestionOption { Id = Guid.CreateVersion7(), Text = "A" });
        draft.Questions.Add(question);
        var answered = NewSurvey(g);
        var answeredQuestion = new SurveyQuestion { Id = Guid.CreateVersion7(), Text = "Q", Type = QuestionType.Text };
        answered.Questions.Add(answeredQuestion);
        var response = NewResponse(answered.Id, g.Student.Id);
        db.Surveys.AddRange(draft, answered);
        db.SurveyResponses.Add(response);
        db.SurveyAnswers.Add(NewAnswer(response.Id, answeredQuestion.Id, text: "A"));
        await db.SaveChangesAsync();

        await db.Surveys.Where(s => s.Id == draft.Id).ExecuteDeleteAsync();
        Assert.False(await db.SurveyQuestions.AnyAsync(q => q.SurveyId == draft.Id));
        Assert.False(await db.SurveyQuestionOptions.AnyAsync(o => o.QuestionId == question.Id));

        Assert.Equal("FK_SurveyResponses_Surveys_SurveyId",
            await ExecViolationAsync(() => db.Surveys.Where(s => s.Id == answered.Id).ExecuteDeleteAsync()));

        await db.SurveyResponses.Where(r => r.Id == response.Id).ExecuteDeleteAsync();
        Assert.False(await db.SurveyAnswers.AnyAsync(a => a.ResponseId == response.Id));
    }

    [Fact]
    public async Task Notifications_are_removed_with_their_user()
    {
        await using var db = Context();
        var user = NewUser();
        db.Users.Add(user);
        db.Notifications.Add(new Notification
        {
            Id = Guid.CreateVersion7(), UserId = user.Id, Type = NotificationType.SurveyAssigned,
            PayloadJson = "{}", CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
        Assert.False(await db.Notifications.AnyAsync(n => n.UserId == user.Id));
    }

    private AppDbContext Context(string? connectionString = null)
    {
        _ = Factory.Services;
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString ?? Factory.ConnectionString).Options);
    }

    private static async Task<List<string>> TablesAsync(AppDbContext db) =>
        await db.Database.SqlQueryRaw<string>(
            "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name")
            .ToListAsync();

    private static async Task<string?> SaveViolationAsync(AppDbContext db)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        return Assert.IsType<PostgresException>(ex.InnerException).ConstraintName;
    }

    private static async Task<string?> ExecViolationAsync(Func<Task> action) =>
        (await Assert.ThrowsAsync<PostgresException>(action)).ConstraintName;

    private static AppUser NewUser()
    {
        var name = $"{Unique("schema")}@test.local";
        return new AppUser { Id = Guid.CreateVersion7(), UserName = name, NormalizedUserName = name.ToUpperInvariant(), Email = name };
    }

    private static async Task<Graph> SeedAsync(AppDbContext db)
    {
        var programId = await db.Programs.Select(p => p.Id).SingleAsync();
        var user = NewUser();
        var group = new Group { Id = Guid.CreateVersion7(), ProgramId = programId, Name = Unique("G") };
        var student = new Student
        {
            Id = Guid.CreateVersion7(), UserId = user.Id, LastName = "Тестова", FirstName = "Анна", Email = user.Email!, GroupId = group.Id
        };
        var teacher = new Teacher { Id = Guid.CreateVersion7(), LastName = "Петров", FirstName = "Пётр" };
        var discipline = new Discipline { Id = Guid.CreateVersion7(), Name = Unique("D") };
        var period = new AcademicPeriod { Id = Guid.CreateVersion7(), Name = Unique("P") };
        db.AddRange(user, group, student, teacher, discipline, period);
        await db.SaveChangesAsync();
        return new Graph(group, student, teacher, discipline, period);
    }

    private static Grade NewGrade(Graph g, int value) => new()
    {
        Id = Guid.CreateVersion7(), StudentId = g.Student.Id, DisciplineId = g.Discipline.Id, PeriodId = g.Period.Id,
        Value = value, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Survey NewSurvey(Graph g) => new()
    {
        Id = Guid.CreateVersion7(), Type = SurveyType.ServiceSurvey, Title = "S", GroupId = g.Group.Id, CreatedAt = DateTimeOffset.UtcNow
    };

    private static SurveyResponse NewResponse(Guid surveyId, Guid studentId) => new()
    {
        Id = Guid.CreateVersion7(), SurveyId = surveyId, StudentId = studentId, SubmittedAt = DateTimeOffset.UtcNow
    };

    private static SurveyAnswer NewAnswer(Guid responseId, Guid questionId, string? text = null, int? intValue = null) => new()
    {
        Id = Guid.CreateVersion7(), ResponseId = responseId, QuestionId = questionId, TextValue = text, IntValue = intValue
    };
}
