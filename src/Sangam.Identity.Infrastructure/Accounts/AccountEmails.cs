using System.Globalization;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Accounts;

/// <summary>Plain-text transactional emails for the account flows. Every message reads without HTML.</summary>
internal static class AccountEmails
{
    public static EmailMessage ForCode(string toEmail, string toName, OneTimeCodePurpose purpose, string code, TimeSpan lifetime)
    {
        int minutes = (int)Math.Round(lifetime.TotalMinutes);
        string minutesText = minutes.ToString(CultureInfo.InvariantCulture);

        return purpose switch
        {
            OneTimeCodePurpose.EmailVerification => new EmailMessage(
                toEmail,
                toName,
                $"{code} is your Sangam verification code",
                $"""
                Hello {toName},

                Your Sangam verification code is:

                    {code}

                Enter it on the page where you created your account. It is valid for {minutesText} minutes and can be used once.

                If you did not create a Sangam account, ignore this email; nothing will happen.

                Sangam - one identity, many homes
                id.sangamid.in
                """),

            OneTimeCodePurpose.PasswordReset => new EmailMessage(
                toEmail,
                toName,
                $"{code} is your Sangam password reset code",
                $"""
                Hello {toName},

                Someone asked to reset the password for your Sangam account. Your reset code is:

                    {code}

                It is valid for {minutesText} minutes and can be used once. Setting a new password signs you out of Sangam on all devices.

                If this was not you, ignore this email; your password will not change.

                Sangam - one identity, many homes
                id.sangamid.in
                """),

            _ => new EmailMessage(
                toEmail,
                toName,
                $"{code} is your Sangam sign-in code",
                $"""
                Hello {toName},

                Your Sangam sign-in code is:

                    {code}

                It is valid for {minutesText} minutes and can be used once.

                If you did not try to sign in, ignore this email and consider changing your password.

                Sangam - one identity, many homes
                id.sangamid.in
                """),
        };
    }
}
