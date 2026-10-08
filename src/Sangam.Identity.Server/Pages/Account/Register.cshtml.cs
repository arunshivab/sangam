using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Apps;
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

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    /// <param name="apps">App directory.</param>
    /// <param name="configuration">Configuration (<c>Sangam:TermsVersion</c>).</param>
    public RegisterModel(IAccountService accounts, IAppDirectory apps, IConfiguration configuration)
    {
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

    /// <summary>The months offered in the date-of-birth list, by number and name.</summary>
    public static IReadOnlyList<(int Number, string Name)> Months { get; } =
    [
        (1, "January"), (2, "February"), (3, "March"), (4, "April"), (5, "May"), (6, "June"),
        (7, "July"), (8, "August"), (9, "September"), (10, "October"), (11, "November"), (12, "December"),
    ];

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

    /// <summary>Renders the form.</summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl(ReturnUrl));
        }

        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        ViewData["PartnerText"] = Partner is null ? null : $"Creating your account for {Partner.DisplayName}";
        return Page();
    }

    /// <summary>Creates the account and moves to email verification.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await ResolvePartnerAsync(_apps, ReturnUrl, cancellationToken);
        ViewData["PartnerText"] = Partner is null ? null : $"Creating your account for {Partner.DisplayName}";
        Strength = PasswordStrength.Evaluate(Password);

        if (!AcceptTerms)
        {
            ModelState.AddModelError(nameof(AcceptTerms), "You need to accept the terms to create an account.");
        }

        ResolveDateOfBirth();

        if (!Genders.TryParse(Gender, out Gender gender))
        {
            ModelState.AddModelError(nameof(Gender), "Select an option.");
        }

        if (!ModelState.IsValid || DateOfBirth is null)
        {
            return Page();
        }

        CountryCode country = CountryCodes.FindByIso(Country) ?? CountryCodes.Default;
        string national = new([.. MobileNumber.Where(char.IsDigit)]);
        if (country.NationalDigits > 0 && national.Length != country.NationalDigits)
        {
            ModelState.AddModelError(nameof(MobileNumber), $"Enter your {country.NationalDigits}-digit {country.Name} mobile number.");
            return Page();
        }

        string mobile = country.DialCode + national;
        RegistrationOutcome outcome = await _accounts.RegisterAsync(
            new RegisterUserCommand(FirstName, LastName, Email, mobile, DateOfBirth.Value, gender, Password, TermsVersion, ClientIp, ClientUserAgent),
            cancellationToken);

        if (!outcome.Result.Succeeded || outcome.UserId is null)
        {
            foreach (AccountError error in outcome.Result.Errors)
            {
                if (error.Field is null)
                {
                    Error = error.Message;
                }
                else
                {
                    ModelState.AddModelError(error.Field == "Mobile" ? nameof(MobileNumber) : error.Field, error.Message);
                }
            }

            return Page();
        }

        await SangamAuthentication.StorePendingAsync(HttpContext, SangamAuthentication.Pending.EmailVerification, outcome.UserId.Value);
        return RedirectToPage("/Account/Verify", new { returnUrl = ReturnUrl });
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

            ModelState.AddModelError(nameof(DateOfBirth), "Enter a real date of birth: day, month and four-digit year.");
            return;
        }

        if (DateOfBirth is null && ModelState.GetFieldValidationState(nameof(DateOfBirth)) != ModelValidationState.Invalid)
        {
            ModelState.AddModelError(nameof(DateOfBirth), "Enter your date of birth.");
        }
    }
}
