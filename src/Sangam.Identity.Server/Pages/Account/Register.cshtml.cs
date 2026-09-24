using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>Screen 2 — registration. All fields required; the strength meter renders server-side.</summary>
public sealed class RegisterModel : AuthPageModel
{
    private readonly IAccountService _accounts;
    private readonly IConfiguration _configuration;

    /// <summary>Initialises the page.</summary>
    /// <param name="accounts">Account service.</param>
    /// <param name="configuration">Configuration (<c>Sangam:TermsVersion</c>).</param>
    public RegisterModel(IAccountService accounts, IConfiguration configuration)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

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

    /// <summary>Country calling code, with or without the leading "+". Defaults to India.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter the country code.")]
    [StringLength(5)]
    public string CountryCode { get; set; } = "+91";

    /// <summary>National mobile number (digits, spaces or dashes).</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your mobile number.")]
    [StringLength(20)]
    public string MobileNumber { get; set; } = string.Empty;

    /// <summary>Date of birth.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Enter your date of birth.")]
    public DateOnly? DateOfBirth { get; set; }

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
    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? LocalRedirect("/account") : Page();

    /// <summary>Creates the account and moves to email verification.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Strength = PasswordStrength.Evaluate(Password);

        if (!AcceptTerms)
        {
            ModelState.AddModelError(nameof(AcceptTerms), "You need to accept the terms to create an account.");
        }

        Gender? gender = ParseGender(Gender);
        if (gender is null)
        {
            ModelState.AddModelError(nameof(Gender), "Select an option.");
        }

        if (!ModelState.IsValid || gender is null || DateOfBirth is null)
        {
            return Page();
        }

        string mobile = "+" + new string([.. CountryCode.Where(char.IsDigit)]) + new string([.. MobileNumber.Where(char.IsDigit)]);
        RegistrationOutcome outcome = await _accounts.RegisterAsync(
            new RegisterUserCommand(FirstName, LastName, Email, mobile, DateOfBirth.Value, gender.Value, Password, TermsVersion, ClientIp, ClientUserAgent),
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
        return RedirectToPage("/Account/Verify");
    }

    private static Gender? ParseGender(string value) => value switch
    {
        "female" => Domain.Enums.Gender.Female,
        "male" => Domain.Enums.Gender.Male,
        "other" => Domain.Enums.Gender.Other,
        "prefer_not_to_say" => Domain.Enums.Gender.PreferNotToSay,
        _ => null,
    };
}
