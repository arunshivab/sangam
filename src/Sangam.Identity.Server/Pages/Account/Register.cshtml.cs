using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
using Sangam.Identity.Application.Partners;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;
using Sangam.Shared.Constants;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Screen 2 — registration. All fields required; the strength meter renders server-side.</summary>
[Antibot]
public sealed class RegisterModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IAppDirectory _apps;
    private readonly IConfiguration _configuration;
    private readonly RegistrationOptions _registration;
    private readonly IInvitationService _invitations;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    /// <param name="apps">App directory.</param>
    /// <param name="configuration">Configuration (<c>Sangam:TermsVersion</c>).</param>
    /// <param name="registration">Registration settings (D-I: invitation-only).</param>
    /// <param name="invitations">Invitations, to let an invited person register (D-I).</param>
    public RegisterModel(IAccountService accounts, IAppDirectory apps, IConfiguration configuration, RegistrationOptions registration, IInvitationService invitations)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _invitations = invitations ?? throw new ArgumentNullException(nameof(invitations));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>Where to continue after verification (the app's authorization request).</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>First name.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your first name.")]
    [StringLength(100)]
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Last name.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your last name.")]
    [StringLength(100)]
    public string LastName { get; set; } = string.Empty;

    /// <summary>Email.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string Email { get; set; } = string.Empty;

    /// <summary>ISO code of the selected country; supplies the dialling code. Defaults to India.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Select your country.")]
    public string Country { get; set; } = CountryCodes.DefaultIso;

    /// <summary>National mobile number (digits, spaces or dashes).</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your mobile number.")]
    [StringLength(20)]
    public string MobileNumber { get; set; } = string.Empty;

    /// <summary>
    /// Date of birth. Browsers post it as three parts (<see cref="BirthDay"/>, <see cref="BirthMonth"/>,
    /// <see cref="BirthYear"/>) so the order never depends on the browser's locale; a single ISO value is still accepted.
    /// </summary>
    [BindProperty]
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>Day of the month of the date of birth (1–31).</summary>
    [BindProperty]
    public string? BirthDay { get; set; }

    /// <summary>Month of the date of birth (1–12, from the month list).</summary>
    [BindProperty]
    public string? BirthMonth { get; set; }

    /// <summary>Four-digit year of the date of birth.</summary>
    [BindProperty]
    public string? BirthYear { get; set; }

    /// <summary>The months offered in the date-of-birth list, by number and by name in the reader's language (PR-18).</summary>
    public static IReadOnlyList<(int Number, string Name)> Months
        => [.. Enumerable.Range(1, 12).Select(n => (n, CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(n)))];

    /// <summary>Gender, as the snake_case value from the select.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Select an option.")]
    public string Gender { get; set; } = string.Empty;

    /// <summary>Password.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Choose a password.")]
    public string Password { get; set; } = string.Empty;

    /// <summary>Terms acceptance.</summary>
    [BindProperty]
    public bool AcceptTerms { get; set; }

    /// <summary>Version of the terms shown.</summary>
    public string TermsVersion => _configuration["Sangam:TermsVersion"] ?? "v1";

    /// <summary>Strength of the current password (empty on first render).</summary>
    public PasswordStrengthResult Strength { get; private set; } = PasswordStrength.Evaluate(null);

    /// <summary>Page-level error.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// D-I: registration is invitation-only and this visit carries no open invitation, and no address is allowed
    /// without one — the page explains instead of showing the form.
    /// </summary>
    public bool Closed { get; private set; }

    /// <summary>The address an open invitation in <see cref="ReturnUrl"/> was sent to, if any.</summary>
    private string? InvitedEmail { get; set; }

    /// <summary>Renders the form.</summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl(ReturnUrl));
        }

        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        ViewData["PartnerText"] = Partner is null ? null : L["Creating your account for {0}", Partner.DisplayName].Value;
        await CheckInvitationAsync(cancellationToken);
        if (InvitedEmail is not null && string.IsNullOrEmpty(Email))
        {
            Email = InvitedEmail;
        }

        return Page();
    }

    /// <summary>Creates the account and moves to email verification.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        ViewData["PartnerText"] = Partner is null ? null : L["Creating your account for {0}", Partner.DisplayName].Value;
        Strength = PasswordStrength.Evaluate(Password);
        await CheckInvitationAsync(cancellationToken);
        if (Closed)
        {
            return Page();
        }

        if (_registration.InvitationOnly
            && !string.Equals(Email.Trim(), InvitedEmail, StringComparison.OrdinalIgnoreCase)
            && !_registration.IsAllowed(Email))
        {
            Error = InvitedEmail is null
                ? L["Sangam is open by invitation only for now. Use the link in your invitation e-mail to create your account."]
                : L["Use the address your invitation was sent to."];
            return Page();
        }

        if (!AcceptTerms)
        {
            ModelState.AddModelError(nameof(AcceptTerms), L["You need to accept the terms to create an account."]);
        }

        ResolveDateOfBirth();

        if (!Genders.TryParse(Gender, out Gender gender))
        {
            ModelState.AddModelError(nameof(Gender), L["Select an option."]);
        }

        if (!ModelState.IsValid || DateOfBirth is null)
        {
            return Page();
        }

        CountryCode country = CountryCodes.FindByIso(Country) ?? CountryCodes.Default;
        string national = new([.. MobileNumber.Where(char.IsDigit)]);
        if (country.NationalDigits > 0 && national.Length != country.NationalDigits)
        {
            ModelState.AddModelError(nameof(MobileNumber), L["Enter your {0}-digit {1} mobile number.", country.NationalDigits, country.Name]);
            return Page();
        }

        string mobile = country.DialCode + national;
        RegistrationOutcome outcome = await _accounts.RegisterAsync(
            new RegisterUserCommand(FirstName, LastName, Email, mobile, DateOfBirth.Value, gender, Password, TermsVersion, ClientIp, ClientUserAgent),
            cancellationToken);

        if (outcome.Concealed)
        {
            // V-09: the same next screen as a real registration; the address's owner has been told by e-mail.
            await SangamAuthentication.StorePendingConcealedRegistrationAsync(HttpContext, Email.Trim());
            return RedirectToPage("/Account/Verify", new { returnUrl = ReturnUrl });
        }

        if (!outcome.Result.Succeeded || outcome.UserId is null)
        {
            foreach (AccountError error in outcome.Result.Errors)
            {
                if (error.Field is null)
                {
                    Error = L[error.Message ?? string.Empty];
                }
                else
                {
                    ModelState.AddModelError(error.Field == "Mobile" ? nameof(MobileNumber) : error.Field, L[error.Message ?? string.Empty]);
                }
            }

            return Page();
        }

        await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.EmailVerification, outcome.UserId.Value);
        return RedirectToPage("/Account/Verify", new { returnUrl = ReturnUrl });
    }

    /// <summary>D-I: finds an open invitation in the return address, and closes the page when one is needed and missing.</summary>
    private async Task CheckInvitationAsync(CancellationToken cancellationToken)
    {
        if (!_registration.InvitationOnly)
        {
            return;
        }

        if (InviteToken(ReturnUrl) is string token
            && await _invitations.GetAsync(token, cancellationToken) is { State: InvitationState.Open } invitation)
        {
            InvitedEmail = invitation.Email;
        }

        Closed = InvitedEmail is null && string.IsNullOrWhiteSpace(_registration.AllowedEmails);
    }

    /// <summary>The invitation token in a return address such as <c>/invite/abc</c>, if any.</summary>
    /// <param name="returnUrl">The return address.</param>
    public static string? InviteToken(string? returnUrl)
    {
        const string Prefix = "/invite/";
        if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = returnUrl[Prefix.Length..];
        int end = rest.IndexOfAny(['?', '#', '/']);
        string token = end < 0 ? rest : rest[..end];
        return token.Length is > 0 and <= 200 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? token : null;
    }

    /// <summary>
    /// Builds <see cref="DateOfBirth"/> from the day, month and year fields when any of them was posted,
    /// and records a field error when the result is missing or not a real date.
    /// </summary>
    private void ResolveDateOfBirth()
    {
        bool partsPosted = !string.IsNullOrWhiteSpace(BirthDay) || !string.IsNullOrWhiteSpace(BirthMonth) || !string.IsNullOrWhiteSpace(BirthYear);
        if (partsPosted)
        {
            ModelState.Remove(nameof(DateOfBirth));
            DateOfBirth = null;
            CultureInfo invariant = CultureInfo.InvariantCulture;
            if (int.TryParse(BirthDay?.Trim(), NumberStyles.None, invariant, out int day)
                && int.TryParse(BirthMonth?.Trim(), NumberStyles.None, invariant, out int month)
                && int.TryParse(BirthYear?.Trim(), NumberStyles.None, invariant, out int year)
                && year is >= 1900 and <= 9999
                && month is >= 1 and <= 12
                && day >= 1 && day <= DateTime.DaysInMonth(year, month))
            {
                DateOfBirth = new DateOnly(year, month, day);
                return;
            }

            ModelState.AddModelError(nameof(DateOfBirth), L["Enter a real date of birth: day, month and four-digit year."]);
            return;
        }

        if (DateOfBirth is null && ModelState.GetFieldValidationState(nameof(DateOfBirth)) != ModelValidationState.Invalid)
        {
            ModelState.AddModelError(nameof(DateOfBirth), L["Enter your date of birth."]);
        }
    }
}
