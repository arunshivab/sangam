using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Server.Tests;

/// <summary>
/// rc.10: the identity server no longer has its own "Your account" page (the portal has the sign-in preference), so tests
/// that need a preference set it through the same service the portal uses.
/// </summary>
internal static class TestAccounts
{
    public static async Task SetPreferenceAsync(IServiceProvider services, string email, SignInMode mode)
    {
        using IServiceScope scope = services.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        Guid id = (await accounts.FindByEmailAsync(email))!.Id;
        await accounts.SetSignInPreferenceAsync(id, mode);
    }
}
