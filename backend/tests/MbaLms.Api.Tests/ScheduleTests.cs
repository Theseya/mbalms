using System.Net;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class ScheduleTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Student_sees_only_lessons_of_own_group()
    {
        var manager = await ManagerAsync();
        var groupA = await CreateGroupAsync(manager);
        var groupB = await CreateGroupAsync(manager);
        var studentA = await StudentAsync(await CreateStudentAsync(manager, groupA.Id));
        var studentB = await StudentAsync(await CreateStudentAsync(manager, groupB.Id));
        var lesson = await CreateLessonAsync(manager, groupA.Id);

        var scheduleA = await studentA.GetJsonAsync<List<StudentLessonDto>>("/api/student/schedule");
        var scheduleB = await studentB.GetJsonAsync<List<StudentLessonDto>>("/api/student/schedule");

        Assert.Contains(scheduleA, l => l.Id == lesson.Id);
        Assert.DoesNotContain(scheduleB, l => l.Id == lesson.Id);
    }

    [Fact]
    public async Task Lesson_time_is_entered_in_app_time_zone_and_stored_as_utc()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var lesson = await CreateLessonAsync(manager, group.Id, "2030-03-10T10:00", "2030-03-10T11:30");

        Assert.Equal(new DateTimeOffset(2030, 3, 10, 7, 0, 0, TimeSpan.Zero), lesson.StartsAt);
        Assert.Equal(new DateTime(2030, 3, 10, 10, 0, 0), lesson.StartsAtLocal);
    }

    [Fact]
    public async Task Lesson_end_before_start_is_rejected()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var teacher = await CreateTeacherAsync(manager);
        var discipline = await CreateDisciplineAsync(manager);
        var res = await manager.PostAsync("/api/manager/lessons", new
        {
            groupId = group.Id, disciplineId = discipline.Id, teacherId = teacher.Id,
            startsAt = "2030-03-10T12:00", endsAt = "2030-03-10T11:00"
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Schedule_changes_notify_only_students_of_the_group()
    {
        var manager = await ManagerAsync();
        var groupA = await CreateGroupAsync(manager);
        var groupB = await CreateGroupAsync(manager);
        var studentA = await StudentAsync(await CreateStudentAsync(manager, groupA.Id));
        var studentB = await StudentAsync(await CreateStudentAsync(manager, groupB.Id));

        var lesson = await CreateLessonAsync(manager, groupA.Id);
        await manager.PutJsonAsync<LessonDto>($"/api/manager/lessons/{lesson.Id}", new
        {
            groupId = groupA.Id, disciplineId = lesson.DisciplineId, teacherId = lesson.TeacherId,
            startsAt = "2030-03-11T10:00", endsAt = "2030-03-11T11:30", location = "Ауд. 101"
        });

        var notesA = await studentA.GetJsonAsync<List<NotificationDto>>("/api/notifications");
        var notesB = await studentB.GetJsonAsync<List<NotificationDto>>("/api/notifications");
        Assert.Equal(2, notesA.Count(n => n.Type == Domain.NotificationType.ScheduleChanged));
        Assert.Empty(notesB);
    }

    [Fact]
    public async Task Student_dashboard_shows_next_lesson()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        var later = await CreateLessonAsync(manager, group.Id, "2031-05-02T10:00", "2031-05-02T11:00");
        var sooner = await CreateLessonAsync(manager, group.Id, "2031-05-01T10:00", "2031-05-01T11:00");

        var dashboard = await student.GetJsonAsync<DashboardDto>("/api/student/dashboard");
        Assert.Equal(sooner.Id, dashboard.NextLesson?.Id);
        Assert.Contains(dashboard.UpcomingLessons, l => l.Id == later.Id);
    }
}
