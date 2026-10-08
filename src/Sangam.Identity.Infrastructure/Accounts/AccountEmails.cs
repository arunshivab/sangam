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

            OneTimeCodePurpose.EmailChange => new EmailMessage(
                toEmail,
                toName,
                $"{code} is your code to confirm this address for Sangam",
                $"""
                Hello {toName},

                You asked to use this address for your Sangam account. Your confirmation code is:

                    {code}

                Enter it in your Sangam account page. It is valid for {minutesText} minutes and can be used once.
                Until you do, your account keeps its current address.

                If you did not ask for this, ignore this email; nothing will change.

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

    /// <summary>Told to the old address after a change, so a hijacked account is noticed (OI-022).</summary>
    public static EmailMessage EmailChangedNotice(string oldEmail, string toName, string newEmailMasked) => new(
        oldEmail,
        toName,
        "Your Sangam email address was changed",
        $"""
        Hello {toName},

        The email address of your Sangam account was changed to {newEmailMasked}. This address will no
        longer be used to sign in.

        If this was you, there is nothing to do. If it was not, write to help@sangamid.in at once from this
        address so we can secure your account.

        Sangam - one identity, many homes
        id.sangamid.in
        """);

    /// <summary>Told to the person when support reset their two-step sign-in (PR-16), so a reset they did not ask for is noticed.</summary>
    public static EmailMessage TwoStepResetNotice(string toEmail, string toName) => new(
        toEmail,
        toName,
        "Your Sangam two-step sign-in was reset",
        $"""
        Hello {toName},

        Sangam support has removed the authenticator app from your account, after confirming who you are,
        because you told us you had lost it and your recovery codes. Every device has been signed out.

        Sign in with your password, then set up an authenticator app again in your account. Keep the new
        recovery codes somewhere safe, away from your phone.

        If you did not ask for this, write to help@sangamid.in at once from this address.

        Sangam - one identity, many homes
        id.sangamid.in
        """);

    /// <summary>An invitation to an organisation and role in an application (PR-13).</summary>
    public static EmailMessage Invitation(string toEmail, string appName, string orgName, string roleName, string link, int days) => new(
        toEmail,
        toEmail,
        $"You are invited to {appName}",
        $"""
        Hello,

        You are invited to join {orgName} in {appName} as {roleName}.

        To accept, open this link and sign in to Sangam with this email address (or create a Sangam
        account with it):

            {link}

        The link works once and for {days.ToString(CultureInfo.InvariantCulture)} days. If you were not expecting this, ignore this
        email; nothing will happen and {appName} will not see your account.

        Sangam - one identity, many homes
        id.sangamid.in
        """);
}
