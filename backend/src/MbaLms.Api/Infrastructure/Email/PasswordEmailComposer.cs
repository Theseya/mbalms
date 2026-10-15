namespace MbaLms.Api.Infrastructure.Email;

public static class PasswordEmailComposer
{
    public const string PurposeInvite = "invite";
    public const string PurposeReset = "password_reset";

    public static EmailMessage Invite(string toEmail, string studentName, string setupUrl) =>
        new(
            toEmail,
            "Приглашение в LMS ДПО",
            $"""
            Здравствуйте, {studentName}!

            Вас пригласили в LMS ДПО.
            Логин (email): {toEmail}

            Установите пароль по ссылке (срок действия ограничен):
            {setupUrl}

            Если вы не ожидали это письмо, проигнорируйте его.
            """,
            Purpose: PurposeInvite);

    public static EmailMessage PasswordReset(string toEmail, string setupUrl) =>
        new(
            toEmail,
            "Сброс пароля LMS ДПО",
            $"""
            Здравствуйте!

            Для вашей учётной записи запрошен сброс пароля.
            Логин (email): {toEmail}

            Установите новый пароль по ссылке (срок действия ограничен):
            {setupUrl}

            Если вы не запрашивали сброс, проигнорируйте это письмо.
            """,
            Purpose: PurposeReset);
}
